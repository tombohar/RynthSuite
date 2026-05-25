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

    // Auto-diagnostic: log the first submit per landblock so the user can
    // verify the ring centre matches the character (not the camera) without
    // having to remember the `/rv debug` opt-in.
    private uint _lastLoggedLandblock;

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
            if ((cellId >> 16) == 0) return;         // portalspace / not in the world
            if ((cellId & 0xFFFF) >= 0x100) return;  // indoor — pose is cell-local, not landblock-local

            // Nav3D world frame is Y-up: (x=east, y=up, z=north). The player
            // pose is AC convention (x=east, y=north, z=up), so swap y/z here.
            // (See RynthAi's TerrainPassabilityOverlay for the same mapping.)
            float radius = MathF.Max(5f, _settings.RadarRangeWorld);
            _host.Nav3DAddRingEx(east, up, north, radius,
                _settings.RadarRingThickness, _settings.RadarRingHeight,
                _settings.RadarRingColorArgb);

            uint landblock = cellId >> 16;
            if (landblock != _lastLoggedLandblock)
            {
                _lastLoggedLandblock = landblock;
                LogRingPlacement(cellId, east, north, up, radius);
            }
        }
        catch (Exception ex)
        {
            _host.Log($"[RynthVision] RadarRing: {ex.Message}");
        }
    }

    // Reports where the ring centre projects on screen. If the projected centre
    // is near the viewport's horizontal middle and roughly at the character's
    // feet, the ring is anchored to the player. If it tracks the camera-look
    // direction instead, either TryGetPlayerPose is returning the wrong object
    // (e.g. SmartBox+0xF8 reassigned during freelook) or the engine view matrix
    // (built from SmartBox+0x08 'viewer' Position) is misaligned with what AC
    // is actually rendering. The screen X/Y in the log makes either case
    // obvious without needing to enable /rv debug.
    private void LogRingPlacement(uint cellId, float east, float north, float up, float radius)
    {
        string screen = "n/a";
        if (_host.HasWorldToScreen && _host.WorldToScreen(east, up, north, out float sx, out float sy))
            screen = $"({sx:F0},{sy:F0})";
        string vp = _host.TryGetViewportSize(out uint vpW, out uint vpH) ? $"{vpW}x{vpH}" : "n/a";
        _host.Log($"[RynthVision] RadarRing placed cell=0x{cellId:X8} centre=({east:F1},{north:F1},{up:F1}) " +
                  $"r={radius:F0} screen={screen} vp={vp}");
    }
}

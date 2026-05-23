using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using RynthCore.Plugin.RynthAi.Raycasting;
using RynthCore.Plugin.RynthVision.Overlays;
using RynthCore.PluginCore;
using RynthCore.TerrainData;

namespace RynthCore.Plugin.RynthVision;

/// <summary>
/// World-space visualization plugin (a RynthCore-native take on Decal's
/// SkunkVision): highlights unclimbable slopes, impassable water, and a
/// radar-range ring using the engine's Nav3D overlay API. Settings are driven by
/// the engine's Avalonia "Vision" panel (via the bridge methods below) or the
/// /rv chat command, and persist to %APPDATA%\RynthCore\rynthvision.json.
/// </summary>
public sealed class RynthVisionPlugin : RynthPluginBase
{
    internal static readonly IntPtr NamePointer    = Marshal.StringToHGlobalAnsi("RynthVision");
    internal static readonly IntPtr VersionPointer = Marshal.StringToHGlobalAnsi("0.1.0");

    private readonly VisionSettings _settings = new();
    private readonly TerrainSampler _terrain = new();

    private RadarRingOverlay? _radarRing;
    private SlopeOverlay? _slopes;
    private WaterOverlay? _water;

    // Diagnostic: /rv debug streams the player pose so we can tell whether it
    // tracks the character or the camera (the radar-ring placement bug).
    private bool _debugPose;
    private int _dbgTick;

    public override int Initialize()
    {
        _settings.Load();
        _radarRing = new RadarRingOverlay(Host, _settings);
        _slopes    = new SlopeOverlay(Host, _settings, _terrain);
        _water     = new WaterOverlay(Host, _settings, _terrain);

        Host.Log("[RynthVision] Initialized. Use the Vision panel, or /rv [radar|slopes|water|watertypes|debug].");
        return 0;
    }

    public override void OnLoginComplete()
    {
        if (_terrain.Initialize())
            Host.Log("[RynthVision] Terrain data ready.");
        else
            Host.Log($"[RynthVision] Terrain data unavailable: {_terrain.Status} (slopes/water disabled).");
    }

    public override void OnLogout() => Host.Log("[RynthVision] Logout.");

    public override void Shutdown() => _terrain.Dispose();

    // Nav3D geometry is buffered every tick and drawn by the engine at end of
    // frame (after the 3D pass). Submitting from OnTick rather than OnRender
    // keeps overlays alive even when the ImGui shell is disabled.
    public override void OnTick()
    {
        if (!Host.HasNav3D)
            return;

        _radarRing?.Submit();
        _slopes?.Submit();
        _water?.Submit();

        if (_debugPose && (++_dbgTick % 30 == 0))
            LogPose();
    }

    public override void OnChatBarEnter(string? text, ref int eat)
    {
        if (string.IsNullOrWhiteSpace(text) || !text.StartsWith("/rv", StringComparison.OrdinalIgnoreCase))
            return;

        eat = 1; // consume — don't send to the server as chat

        string arg = text.Length > 3 ? text.Substring(3).Trim().ToLowerInvariant() : string.Empty;
        switch (arg)
        {
            case "radar":      _settings.ShowRadarRing = !_settings.ShowRadarRing; break;
            case "slopes":     _settings.ShowUnclimbableSlopes = !_settings.ShowUnclimbableSlopes; break;
            case "water":      _settings.ShowImpassableWater = !_settings.ShowImpassableWater; break;
            case "watertypes": InspectTerrain(); return;
            case "debug":      _debugPose = !_debugPose; Host.Log($"[RynthVision] pose debug = {_debugPose}"); return;
            case "":           break;
            default:
                Host.Log($"[RynthVision] Unknown option '{arg}'. Use radar | slopes | water | watertypes | debug.");
                return;
        }

        _settings.Save();
        Host.Log($"[RynthVision] radar={_settings.ShowRadarRing} slopes={_settings.ShowUnclimbableSlopes} water={_settings.ShowImpassableWater}");
    }

    // ── Bridge methods called from PluginExports (engine Avalonia panel) ───────

    internal string BuildSettingsJson() => _settings.ToJson();

    internal void ApplySettingsJson(string json)
    {
        _settings.ApplyJson(json);
        _settings.Save();
    }

    // Streams pose + the terrain height under the player + the screen projection
    // of the exact ring-centre coords. Lets us tell whether the ring's height
    // (pose.z) matches the ground (terrainZ) and whether the centre projects to
    // where the player actually is on screen (≈ viewport centre).
    private void LogPose()
    {
        if (!Host.TryGetPlayerPose(out uint cellId, out float x, out float y, out float z,
                out _, out _, out _, out _))
        {
            Host.Log("[RynthVision] DBG pose unavailable");
            return;
        }

        float terrainZ = float.NaN;
        if (_terrain.IsReady && (cellId & 0xFFFF) < 0x100 && (cellId >> 16) != 0)
        {
            LandblockData? lb = _terrain.LoadLandblock(cellId >> 16);
            if (lb != null)
                terrainZ = lb.GetVertexZ(Math.Clamp((int)(x / 24f), 0, 8), Math.Clamp((int)(y / 24f), 0, 8));
        }

        // Project the exact ring-centre coords (east=x, up=z, north=y) to screen.
        string w2s = "n/a";
        if (Host.HasWorldToScreen && Host.WorldToScreen(x, z, y, out float sx, out float sy))
            w2s = $"({sx:F0},{sy:F0})";
        Host.TryGetViewportSize(out uint vpW, out uint vpH);

        Host.Log($"[RynthVision] DBG cell=0x{cellId:X8} pose=({x:F1},{y:F1},{z:F1}) terrainZ={terrainZ:F1} w2s={w2s} vp={vpW}x{vpH}");
    }

    /// <summary>
    /// Logs the terrain-type index at the player's exact cell plus all distinct
    /// types in the landblock — stand on impassable vs passable water to find
    /// which indices belong in VisionSettings.WaterTerrainTypes.
    /// </summary>
    internal void InspectTerrain()
    {
        if (!_terrain.IsReady)
        {
            Host.Log($"[RynthVision] Terrain not ready: {_terrain.Status}");
            return;
        }
        if (!Host.TryGetPlayerPose(out uint cellId, out float px, out float py, out _, out _, out _, out _, out _) ||
            (cellId & 0xFFFF) >= 0x100 || (cellId >> 16) == 0)
        {
            Host.Log("[RynthVision] Stand outdoors to inspect terrain types.");
            return;
        }
        LandblockData? lb = _terrain.LoadLandblock(cellId >> 16);
        if (lb == null)
        {
            Host.Log("[RynthVision] No terrain data for this landblock.");
            return;
        }

        int cx = Math.Clamp((int)(px / 24f), 0, 8);
        int cy = Math.Clamp((int)(py / 24f), 0, 8);
        int hereType = TerrainSampler.GetTerrainType(lb, cx, cy);

        var seen = new SortedSet<int>();
        for (int ix = 0; ix < 9; ix++)
            for (int iy = 0; iy < 9; iy++)
                seen.Add(TerrainSampler.GetTerrainType(lb, ix, iy));

        Host.Log($"[RynthVision] At cell ({cx},{cy}) terrain type = {hereType}. Landblock types: {string.Join(", ", seen)}");
    }
}

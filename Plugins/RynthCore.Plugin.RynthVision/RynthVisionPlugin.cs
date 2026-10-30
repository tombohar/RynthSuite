using System;
using System.Collections.Generic;
using System.Diagnostics;
using RynthCore.Plugin.RynthAi.Raycasting;
using RynthCore.Plugin.RynthVision.Overlays;
using RynthCore.Plugin.Shared;
using RynthCore.PluginCore;
using RynthCore.TerrainData;

namespace RynthCore.Plugin.RynthVision;

/// <summary>
/// World-space visualization plugin (a RynthCore-native take on Decal's
/// SkunkVision): highlights unclimbable slopes, impassable water, and a
/// radar-range ring using the engine's Nav3D overlay API, and lights dark
/// dungeons (SkunkVision's Dungeon Light; RynthVisionPlugin.Light.cs, through
/// RynthCore's ambient light floor). Settings come from RynthVision's own
/// Vision window (2026-10-27, Ui/VisionWindow.cs: it takes the engine's Vision
/// panel's place on an engine with plugin API v91; RynthVisionPlugin.Ui.cs), the
/// engine's Vision panels on an older engine (its ImGui face, and the Avalonia
/// twin, through the JSON exports in PluginExports), or the /rv chat command,
/// and persist to %APPDATA%\RynthCore\rynthvision.json.
///
/// Terrain data (the portal/cell .dat files) is opened on the first in-world
/// tick that has slopes or water switched on, or the first EnvCell the
/// dungeon light is on in, never while those are off.
/// </summary>
public sealed partial class RynthVisionPlugin : RynthPluginBase
{
    // An EnvCell pose more than this far below the ground is a dungeon under
    // the landscape, not a building interior (see IsOnLandscape).
    private const float BelowGroundLimit = 10f;

    private readonly VisionSettings _settings = new();
    private readonly TerrainSampler _terrain = new();
    private readonly TickContext _ctx = new();
    private LandblockWindow? _window;

    private RadarRingOverlay? _radarRing;
    private SlopeOverlay? _slopes;
    private WaterOverlay? _water;

    // Lazy terrain init: one attempt per login, or per time slopes/water are
    // switched back on, so a missing dat doesn't retry every tick.
    private bool _terrainAttempted;
    private bool _terrainWanted;

    // Diagnostic: /rv debug streams the player pose and turns on the
    // per-landblock ring/water logs.
    private bool _debug;
    private int _dbgTick;

    // What /rv says, queued and written by the tick (Plugins/Shared/CommandReply.cs).
    private CommandReply _reply = null!;

    public override int Initialize()
    {
        _reply = new CommandReply("[RynthVision]", (t, c) => Host.WriteToChat(t, c), t => Host.Log(t));
        // The Vision window draws with ImGui.NET bound to the engine's cimgui, in its look.
        RynthCore.Plugin.Shared.ImGuiNativeBinding.Ensure(msg => Host.Log("[RynthVision] " + msg));
        RynthCore.Plugin.Shared.RynthUi.RynthUi.Init(Host);
        _hostsWindow = Ui.VisionManifest.HostsWindow(Host.HasAmbientLight, Host.HasSetPanelOpen, Host.HasSettingsDrawers, Host.HasUiTheme);
        _settings.Load();
        _window    = new LandblockWindow(_terrain);
        _radarRing = new RadarRingOverlay(Host, _settings);
        _slopes    = new SlopeOverlay(Host, _settings);
        _water     = new WaterOverlay(Host, _settings);

        Host.Log("[RynthVision] Initialized" + (_hostsWindow ? " (draws the Vision window)" : " (RynthCore's own Vision panel)") +
                 ". Use the Vision panel, or /rv [radar|slopes|water|watertypes|debug|light].");
        return 0;
    }

    public override void OnLoginComplete()
    {
        _terrainAttempted = false;
        ResetLightPlace();
    }

    public override void OnLogout() => Host.Log("[RynthVision] Logout.");

    public override void Shutdown()
    {
        ClearLightFloor();
        _terrain.Dispose();
    }

    // Nav3D geometry is buffered every tick and drawn by the engine at end of
    // frame (after the 3D pass). Submitting from OnTick rather than OnRender
    // keeps overlays alive even when the ImGui shell is disabled.
    public override void OnTick()
    {
        // The window's, /rv's and the light's changes first: this tick draws with them.
        TickUi();
        _reply.Flush();

        // One pose for the light and every overlay this tick.
        bool inWorld = Host.TryGetPlayerPose(out uint cellId, out float x, out float y, out float z,
            out _, out _, out _, out _) && (cellId >> 16) != 0;

        // The dungeon light runs in and out of the world (out of it, it clears the floor).
        TickLight(inWorld ? cellId : 0);

        if (!Host.HasNav3D || !inWorld)
            return; // no overlays, or not in the world / portalspace

        bool wanted = _settings.ShowUnclimbableSlopes || _settings.ShowImpassableWater;
        if (wanted && !_terrainWanted) _terrainAttempted = false; // switched on again: retry
        _terrainWanted = wanted;
        if (wanted && !_terrain.IsReady && !_terrainAttempted)
            InitTerrain();

        TickContext ctx = _ctx;
        ctx.CellId = cellId;
        ctx.X = x; ctx.Y = y; ctx.Z = z;
        ctx.Debug = _debug;
        ctx.Terrain = null;
        if (_terrain.IsReady && _window != null)
        {
            _window.Reset(ctx.LandblockX, ctx.LandblockY);
            ctx.Terrain = _window;
        }
        ctx.OnLandscape = IsOnLandscape(ctx);

        _radarRing?.Submit(ctx);
        _slopes?.Submit(ctx);
        _water?.Submit(ctx);

        if (_debug && (++_dbgTick % 30 == 0))
            LogPose();
    }

    private bool InitTerrain()
    {
        _terrainAttempted = true;
        var sw = Stopwatch.StartNew();
        bool ok = _terrain.Initialize();
        if (ok)
            Host.Log($"[RynthVision] Terrain data ready ({sw.Elapsed.TotalMilliseconds:F0} ms).");
        else
            Host.Log($"[RynthVision] Terrain data unavailable: {_terrain.Status} (slopes/water disabled).");
        return ok;
    }

    /// <summary>
    /// Whether the pose is in landscape coordinates. AC keeps every Position
    /// landblock-relative, EnvCells included (the classic coordinates formula
    /// lbX*8 + x/24 works unchanged indoors), so building interiors on the
    /// landscape sit in the same frame as the outdoor cells around them. An
    /// EnvCell counts when its landblock has real terrain (ACE treats a
    /// landblock whose heights are all 0 as a dungeon block), the position is
    /// inside the block, and it isn't well below the ground (a dungeon under
    /// the landscape). Deciding that needs the terrain, so without it EnvCells
    /// stay off, as before.
    /// </summary>
    private static bool IsOnLandscape(TickContext ctx)
    {
        if ((ctx.CellId & 0xFFFF) < 0x100) return true;
        if (ctx.Terrain == null) return false;
        if (ctx.X < 0f || ctx.Y < 0f || ctx.X >= TickContext.BlockLength || ctx.Y >= TickContext.BlockLength) return false;

        LandblockData? lb = ctx.Terrain.Get(0, 0);
        if (lb == null || lb.HeightIndices == null) return false;
        bool hasTerrain = false;
        foreach (byte h in lb.HeightIndices)
            if (h != 0) { hasTerrain = true; break; }
        if (!hasTerrain) return false;

        return ctx.Z >= TerrainSampler.GetTerrainZ(lb, ctx.X, ctx.Y) - BelowGroundLimit;
    }

    public override void OnChatBarEnter(string? text, ref int eat)
    {
        // "/rv" alone or followed by whitespace; "/rvanything" isn't ours.
        if (string.IsNullOrWhiteSpace(text) || !text.StartsWith("/rv", StringComparison.OrdinalIgnoreCase)
            || (text.Length > 3 && !char.IsWhiteSpace(text[3])))
            return;

        eat = 1; // consume — don't send to the server as chat

        string arg = text.Length > 3 ? text.Substring(3).Trim().ToLowerInvariant() : string.Empty;

        // Every /rv answers in chat (2026-10-28, VisionCommands.cs; Plugins/Shared/CommandReply.cs). RynthVision
        // can't tell a typed /rv from a meta's, so each one does.
        VisionVerb verb = VisionCommands.Parse(arg, out string lightArgs);

        // "/rv light ..." (Light/DungeonLight.ParseCommand): applied and answered in chat on the tick.
        if (verb == VisionVerb.Light)
        {
            QueueLightCommand(lightArgs);
            return;
        }

        // The settings are the tick's (the Vision window reads the copy it publishes): toggles go through
        // its queue, like the window's own changes.
        Action<VisionSettings>? toggle;
        switch (verb)
        {
            case VisionVerb.Radar:      toggle = static st => st.ShowRadarRing = !st.ShowRadarRing; break;
            case VisionVerb.Slopes:     toggle = static st => st.ShowUnclimbableSlopes = !st.ShowUnclimbableSlopes; break;
            case VisionVerb.Water:      toggle = static st => st.ShowImpassableWater = !st.ShowImpassableWater; break;
            case VisionVerb.WaterTypes: QueueInspectTerrain(toChat: true); return;
            case VisionVerb.Debug:
                _debug = !_debug;
                Host.Log($"[RynthVision] debug = {_debug}");
                _reply.Ok(VisionCommands.Debug(_debug));
                return;
            case VisionVerb.Status:     toggle = null; break;
            default:
                Host.Log($"[RynthVision] Unknown option '{arg}'. Use radar | slopes | water | watertypes | debug | light.");
                _reply.Fail(VisionCommands.Unknown(arg));
                return;
        }

        _uiActions.Enqueue(() =>
        {
            toggle?.Invoke(_settings);
            _settings.Save();
            MarkSettingsChanged();
            Host.Log($"[RynthVision] radar={_settings.ShowRadarRing} slopes={_settings.ShowUnclimbableSlopes} water={_settings.ShowImpassableWater}");
            _reply.Ok(verb switch
            {
                VisionVerb.Radar => VisionCommands.Switched(verb, _settings.ShowRadarRing),
                VisionVerb.Slopes => VisionCommands.Switched(verb, _settings.ShowUnclimbableSlopes),
                VisionVerb.Water => VisionCommands.Switched(verb, _settings.ShowImpassableWater),
                _ => VisionCommands.State(_settings.ShowRadarRing, _settings.ShowUnclimbableSlopes, _settings.ShowImpassableWater),
            });
        });
    }

    // ── Bridge methods called from PluginExports (engine Vision panels; pump thread, as the tick) ──

    internal string BuildSettingsJson() => _settings.ToJson();

    /// <summary>Applies any subset of the settings keys (panels send one field at a time).</summary>
    internal void ApplySettingsJson(string json)
    {
        _settings.ApplyJson(json);
        _settings.Save();
        MarkSettingsChanged();
    }

    // Streams pose + the terrain height under the player + the screen projection
    // of the exact ring-centre coords. Lets us tell whether the ring's height
    // (pose.z) matches the ground (terrainZ) and whether the centre projects to
    // where the player actually is on screen (≈ viewport centre).
    private void LogPose()
    {
        TickContext ctx = _ctx;
        float terrainZ = float.NaN;
        if (ctx.Terrain != null && ctx.Terrain.TryGetTerrainZ(ctx.X, ctx.Y, out float tz))
            terrainZ = tz;

        // Project the exact ring-centre coords (east=x, up=z, north=y) to screen.
        string w2s = "n/a";
        if (Host.HasWorldToScreen && Host.WorldToScreen(ctx.X, ctx.Z, ctx.Y, out float sx, out float sy))
            w2s = $"({sx:F0},{sy:F0})";
        Host.TryGetViewportSize(out uint vpW, out uint vpH);

        Host.Log($"[RynthVision] DBG cell=0x{ctx.CellId:X8} pose=({ctx.X:F1},{ctx.Y:F1},{ctx.Z:F1}) terrainZ={terrainZ:F1} " +
                 $"landscape={ctx.OnLandscape} w2s={w2s} vp={vpW}x{vpH}");
    }

    /// <summary>
    /// Logs the terrain-type index at the player's exact cell plus all distinct
    /// types in the landblock — stand on impassable vs passable water to find
    /// which indices belong in VisionSettings.WaterTerrainTypes. An explicit
    /// request, so it opens the terrain data if the overlays haven't.
    /// </summary>
    internal void InspectTerrain() => InspectTerrain(toChat: false);

    /// <summary><paramref name="toChat"/>: /rv watertypes, which answers in chat as well as the log.</summary>
    internal void InspectTerrain(bool toChat)
    {
        if (!_terrain.IsReady && !InitTerrain())
        {
            if (toChat) _reply.Fail($"Terrain data unavailable: {_terrain.Status}.");
            return;
        }
        if (!Host.TryGetPlayerPose(out uint cellId, out float px, out float py, out _, out _, out _, out _, out _) ||
            (cellId & 0xFFFF) >= 0x100 || (cellId >> 16) == 0)
        {
            Host.Log("[RynthVision] Stand outdoors to inspect terrain types.");
            if (toChat) _reply.Fail("Stand outdoors to inspect terrain types.");
            return;
        }
        LandblockData? lb = _terrain.LoadLandblock(cellId >> 16);
        if (lb == null)
        {
            Host.Log("[RynthVision] No terrain data for this landblock.");
            if (toChat) _reply.Fail("No terrain data for this landblock.");
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
        if (toChat) _reply.Ok($"At cell ({cx},{cy}) terrain type = {hereType}. Landblock types: {string.Join(", ", seen)}");
    }
}

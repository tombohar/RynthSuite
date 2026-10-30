// ============================================================================
//  RynthVision - RynthVisionPlugin.Light.cs
//  The dungeon light (2026-10-27; rules in Light/DungeonLight.cs): every tick
//  it works out where the player is the way AC does (outdoors, a building's
//  room, a dungeon cell) and hands RynthCore the ambient light floor for it
//  (RynthCoreHost.SetAmbientLightFloor, plugin API v91): the light's colour
//  times its level where the light applies, 0 everywhere else, out of the
//  world, and when it is off. Drawing only, in this client.
//
//  Where: an EnvCell's "sees outside" flag comes from the cell dat (the
//  TerrainSampler the overlays use, opened the first time the light is on in
//  an EnvCell; a cell is read once, when the player enters it). While the
//  light is off nothing is opened and no floor is ever sent.
//
//  Threads: /rv light (AC's thread) and the Vision window's Light tab (AC's
//  render thread; Ui/VisionWindow.cs since 2026-10-27, the separate Dungeon
//  Light window is gone) queue their changes (RynthVisionPlugin.Ui.cs); the
//  tick (the plugin pump) applies them, saves the settings, sends the floor and
//  publishes the LightView the tab draws. The floor is sent on every change and
//  again every 2 s while it is set (an /rc hooks switch shows), and cleared on
//  Shutdown (RynthCore also clears it when plugins unload).
// ============================================================================

using System;
using System.Collections.Generic;
using RynthCore.Plugin.RynthVision.Light;
using RynthCore.Plugin.RynthVision.Ui;
using RynthCore.PluginSdk;
using RynthCore.TerrainData;

namespace RynthCore.Plugin.RynthVision;

public sealed partial class RynthVisionPlugin
{
    private const long LightResendMs = 2000;
    private const long LightViewMs = 500;
    private const int SeenOutsideCacheCap = 1024;

    private uint _lightCell = uint.MaxValue;     // the cell _lightPlace is for (MaxValue: none yet)
    private bool _lightPlaceExact;               // _lightPlace came from the dat (else "unknown EnvCell")
    private LightPlace _lightPlace;
    private readonly Dictionary<uint, bool> _seenOutside = new();
    private bool _lightTerrainTried;             // once per login: the light opened (or tried) the dats
    private uint _floorSent;                     // the floor RynthCore has from us (0 = none)
    private long _floorSentMs;
    private long _nextLightViewMs;
    private bool _lightViewDirty = true;
    private volatile LightView _lightView = LightView.Empty;

    /// <summary>
    /// Plugin tick (pump thread), in the world or not, after TickUi applied the queued changes:
    /// <paramref name="cellId"/> 0 = not in the world.
    /// </summary>
    private void TickLight(uint cellId)
    {
        bool on = _settings.LightOn;
        if (cellId != _lightCell || (on && !_lightPlaceExact))
            ClassifyCell(cellId, on);

        bool hasApi = Host.HasAmbientLight;
        uint floor = DungeonLight.FloorFor(on, (LightScope)_settings.LightScope, _settings.LightStrength,
            _settings.LightColorArgb, _lightPlace);
        long now = Environment.TickCount64;
        if (hasApi && (floor != _floorSent || (floor != 0 && now - _floorSentMs >= LightResendMs)))
        {
            Host.SetAmbientLightFloor(floor);
            if (floor != _floorSent) _lightViewDirty = true;
            _floorSent = floor;
            _floorSentMs = now;
        }

        if (_lightViewDirty || now >= _nextLightViewMs)
        {
            _lightViewDirty = false;
            _nextLightViewMs = now + LightViewMs;
            LightStatus status = CurrentLightStatus(hasApi);
            _lightView = new LightView(on, _settings.LightStrength, (LightScope)_settings.LightScope, _settings.LightColorArgb,
                DungeonLight.StatusLine(status), DungeonLight.IsWarning(status));
        }
    }

    private LightStatus CurrentLightStatus(bool hasApi)
    {
        uint client = 0, applied = 0;
        bool live = hasApi && Host.TryGetAmbientLight(out client, out applied);
        return new LightStatus(_settings.LightOn, (LightScope)_settings.LightScope, _settings.LightStrength, _lightPlace,
            hasApi, live, client, applied);
    }

    /// <summary>
    /// Where <paramref name="cellId"/> is. An EnvCell is read from the cell dat only while the light is
    /// on (<paramref name="exact"/>); otherwise it stays "unknown" (a dungeon) until it is needed.
    /// </summary>
    private void ClassifyCell(uint cellId, bool exact)
    {
        bool envCell = (cellId >> 16) != 0 && (cellId & 0xFFFF) >= 0x100;
        bool? seenOutside = null;
        bool known = true;
        if (envCell)
        {
            if (exact) seenOutside = SeenOutside(cellId);
            known = exact && seenOutside != null;
        }
        LightPlace place = DungeonLight.Classify(cellId, seenOutside);
        if (place != _lightPlace) _lightViewDirty = true;
        _lightPlace = place;
        _lightCell = cellId;
        // A cell the dat couldn't answer for stays "exact" too: asking again every tick won't help.
        _lightPlaceExact = known || (exact && (_lightTerrainTried || _terrain.IsReady));
    }

    /// <summary>The EnvCell's "sees outside" flag from the cell dat (cached per cell); null when unknown.</summary>
    private bool? SeenOutside(uint cellId)
    {
        if (_seenOutside.TryGetValue(cellId, out bool cached)) return cached;
        if (!_terrain.IsReady && !_lightTerrainTried)
        {
            _lightTerrainTried = true;
            InitTerrain();
        }
        if (!_terrain.IsReady || !_terrain.TryGetEnvCellFlags(cellId, out uint flags)) return null;
        bool seen = (flags & TerrainSampler.EnvCellSeenOutside) != 0;
        if (_seenOutside.Count >= SeenOutsideCacheCap) _seenOutside.Clear();
        _seenOutside[cellId] = seen;
        return seen;
    }

    /// <summary>A new login: where the player is gets worked out again; the dats may be tried again.</summary>
    private void ResetLightPlace()
    {
        _lightCell = uint.MaxValue;
        _lightPlaceExact = false;
        _lightTerrainTried = false;
        _lightViewDirty = true;
    }

    /// <summary>Shutdown: RynthCore's floor back to none (RynthCore clears it on unload too).</summary>
    private void ClearLightFloor()
    {
        try
        {
            if (_floorSent != 0 && Host.HasAmbientLight) Host.SetAmbientLightFloor(0);
        }
        catch { }
        _floorSent = 0;
    }

    // ── /rv light (queued; the tick applies it) ──────────────────────────

    /// <summary>"/rv light ..." (AC's thread): parsed here, applied and answered on the tick.</summary>
    private void QueueLightCommand(string args)
    {
        LightCommand cmd = DungeonLight.ParseCommand(args);
        _uiActions.Enqueue(() => RunLightCommand(cmd));
    }

    private void RunLightCommand(LightCommand cmd)
    {
        VisionSettings s = _settings;
        switch (cmd.Kind)
        {
            case LightCommandKind.Toggle: s.LightOn = !s.LightOn; break;
            case LightCommandKind.On: s.LightOn = true; break;
            case LightCommandKind.Off: s.LightOn = false; break;
            case LightCommandKind.Strength: s.LightStrength = cmd.Strength; s.LightOn = true; break;
            case LightCommandKind.Scope: s.LightScope = (int)cmd.Scope; break;
            case LightCommandKind.Color: s.LightColorArgb = cmd.ColorArgb; break;
            case LightCommandKind.Show:
                // The Vision window, on its Light tab (2026-10-27: the light lives in the Vision window).
                if (_hostsWindow && Host.SetPanelOpen(VisionManifest.Title, RynthPanelOpen.Open) >= 0)
                {
                    RequestTab(VisionTab.Light);
                    LightChat("Vision window shown, on its Light tab.");
                    return;
                }
                LightChat("The Vision window's Light tab needs a newer RynthCore (plugin API 91); " + DungeonLight.Usage, ok: false);
                return;
            case LightCommandKind.Status:
                LightChat("Dungeon light: " + DungeonLight.StatusLine(CurrentLightStatus(Host.HasAmbientLight)));
                return;
            case LightCommandKind.Help:
                LightChat(DungeonLight.Usage);
                return;
            default:
                LightChat(cmd.Error + " " + DungeonLight.Usage, ok: false);
                return;
        }
        s.Save();
        MarkSettingsChanged();
        LightChat(DungeonLight.Summary(s.LightOn, s.LightStrength, (LightScope)s.LightScope, s.LightColorArgb, Host.HasAmbientLight));
    }

    /// <summary>/rv light's answer: the log, and chat through the reply queue (Plugins/Shared/CommandReply.cs).</summary>
    private void LightChat(string text, bool ok = true)
    {
        Host.Log("[RynthVision] " + text);
        _reply.Say(ok, text);
    }
}

// ============================================================================
//  RynthVision - RynthVisionPlugin.Ui.cs
//  The Vision window's side of the plugin (2026-10-27; Ui/VisionWindow.cs,
//  Ui/VisionManifest.cs): RynthVision draws the whole Vision window, in the
//  engine's Vision panel's place, on an engine with plugin API v91 (_hostsWindow,
//  decided once in Initialize; an older engine keeps its own panel).
//
//  Threads: the window (AC's render thread), /rv (AC's thread) and the light's
//  commands queue their changes here; the tick (the plugin pump) applies them
//  first thing, saves rynthvision.json and publishes a fresh copy of the
//  settings for the window (SettingsView) whenever anything changed them (a
//  change here, or the engine's panels through PluginExports, on the pump too).
//  /rv light show asks the window for its Light tab (_tabRequest).
// ============================================================================

using System;
using System.Collections.Concurrent;
using System.Threading;
using RynthCore.Plugin.RynthVision.Light;
using RynthCore.Plugin.RynthVision.Ui;

namespace RynthCore.Plugin.RynthVision;

public sealed partial class RynthVisionPlugin
{
    private readonly ConcurrentQueue<Action> _uiActions = new();
    private volatile SettingsView? _settingsView;
    private int _settingsVersion;
    private bool _settingsDirty = true;          // pump thread
    private bool _hostsWindow;                   // Initialize: the engine hosts the Vision window
    private int _tabRequest = -1;                // a VisionTab the window should show (-1 none)
    private VisionWindow? _visionWindow;         // render thread

    /// <summary>The manifest RynthCore hosts the Vision window from (no window on an older engine).</summary>
    internal string SectionsJson => VisionManifest.Json(_hostsWindow);

    /// <summary>The settings as the tick last published them (the window's copy source). Any thread.</summary>
    internal SettingsView? Settings => _settingsView;

    /// <summary>The dungeon light's state and status line, as the tick last published them. Any thread.</summary>
    internal LightView LightState => _lightView;

    /// <summary>Tick, first thing: the queued changes, then a fresh copy for the window if anything changed.</summary>
    private void TickUi()
    {
        while (_uiActions.TryDequeue(out Action? act))
        {
            try { act(); }
            catch (Exception ex) { Host.Log("[RynthVision] Settings change: " + ex.Message); }
        }
        if (!_settingsDirty) return;
        _settingsDirty = false;
        _settingsView = new SettingsView(++_settingsVersion, _settings.Clone());
    }

    /// <summary>The settings changed (pump thread): republish them, and the light's view.</summary>
    private void MarkSettingsChanged()
    {
        _settingsDirty = true;
        _lightViewDirty = true;
    }

    /// <summary>Applies <paramref name="change"/> on the tick and saves when <paramref name="save"/>. Any thread.</summary>
    internal void QueueSettingsChange(Action<VisionSettings> change, bool save = true) => _uiActions.Enqueue(() =>
    {
        change(_settings);
        if (save) _settings.Save();
        MarkSettingsChanged();
    });

    /// <summary>"Log terrain types here": on the tick (it may open the terrain data). Any thread.</summary>
    internal void QueueInspectTerrain(bool toChat = false) => _uiActions.Enqueue(() => InspectTerrain(toChat));

    /// <summary>Asks the window to show <paramref name="tab"/> on its next draw. Any thread.</summary>
    private void RequestTab(VisionTab tab) => Volatile.Write(ref _tabRequest, (int)tab);

    /// <summary>The tab /rv light show asked for, once. Render thread.</summary>
    internal bool TakeTabRequest(out VisionTab tab)
    {
        int t = Interlocked.Exchange(ref _tabRequest, -1);
        tab = (VisionTab)Math.Max(0, t);
        return t >= 0;
    }

    /// <summary>RynthPluginRenderEmbed: draws <paramref name="surface"/> (":popout" already off). Render thread.</summary>
    internal bool RenderEmbed(string surface)
    {
        if (!_hostsWindow || !surface.Equals(VisionManifest.Surface, StringComparison.Ordinal)) return false;
        _visionWindow ??= new VisionWindow(this, Host);
        _visionWindow.Draw();
        return true;
    }
}

// ============================================================================
//  RynthNet - RynthNetPlugin.Characters.cs
//  The Characters window, RynthNet's own (2026-10-21; RynthCore drew it before):
//  the sections manifest declares it ("windows", plugin API v89: RynthCore hosts
//  it and gives it a bar button), RynthPluginRenderEmbed draws it
//  (Characters/CharactersWindow.cs), and the tick rebuilds its rows twice a
//  second while it is on screen (Characters/CharactersRows.cs). The window's
//  clicks are queued here and run on the tick: selecting a character, and /rn
//  tell / followme through the same queue as typed commands.
// ============================================================================

using System;
using System.Collections.Concurrent;
using System.IO;
using RynthCore.Install;
using RynthCore.Plugin.RynthNet.Characters;
using RynthCore.Plugin.Shared.RynthUi;
using RynthCore.PluginSdk;

namespace RynthCore.Plugin.RynthNet;

public sealed partial class RynthNetPlugin
{
    private const string CharactersSurface = CharactersManifest.Surface;
    private const long CharactersRefreshMs = 500;
    /// <summary>Rows are built only while the window was drawn this recently.</summary>
    private const long CharactersShownMs = 2000;

    private CharactersWindow? _charsWindow;                          // render thread
    private volatile CharactersView _charsView = CharactersView.Empty;
    private long _charsDrawnMs = long.MinValue / 2;                  // render thread writes, tick reads
    private long _nextCharsMs;
    private string? _charsJson;
    private NetCharSnapshot? _charsSnap;
    private readonly ConcurrentQueue<Action> _uiActions = new();
    private HostGame? _game;

    /// <summary>RynthPluginRenderEmbed: draws <paramref name="surface"/> (":popout" already off). Render thread.</summary>
    internal bool RenderEmbed(string surface, out float wantHeight)
    {
        wantHeight = 0;
        if (!surface.Equals(CharactersSurface, StringComparison.Ordinal)) return false;
        System.Threading.Volatile.Write(ref _charsDrawnMs, Environment.TickCount64);
        AtlasNames.EnsureLoading(Path.Combine(RynthInstallPaths.CoreDir, "NavData", "locations.json"));
        _charsWindow ??= new CharactersWindow(
            select: id => _uiActions.Enqueue(() => { if (id != 0) Host.SelectItem(id); }),
            tell: (name, command) => _localCommands.Enqueue(("/rn tell " + name + ", " + command, false)),
            followMeAll: () => _localCommands.Enqueue(("/rn followme", false)),
            heading: CompassHeading);
        _charsWindow.Draw(_charsView);
        return true;
    }

    /// <summary>Plugin tick: the window's queued clicks, and its rows while it is on screen.</summary>
    private void TickCharacters()
    {
        while (_uiActions.TryDequeue(out Action? act))
        {
            try { act(); }
            catch (Exception ex) { Host.Log("[RynthNet] Characters: " + ex.Message); }
        }
        long now = Environment.TickCount64;
        if (now - System.Threading.Volatile.Read(ref _charsDrawnMs) > CharactersShownMs || now < _nextCharsMs) return;
        _nextCharsMs = now + CharactersRefreshMs;
        string json = _snapshot;
        if (!ReferenceEquals(json, _charsJson))
        {
            _charsJson = json;
            _charsSnap = CharactersModel.Parse(json);
        }
        _game ??= new HostGame(this);
        _charsView = CharactersRows.Build(_charsSnap ?? NetCharSnapshot.Empty, _game);
    }

    /// <summary>Your compass heading (0 = north, clockwise) from the pose's yaw quaternion. Render thread.</summary>
    private (bool Ok, float Heading) CompassHeading()
    {
        if (!Host.TryGetPlayerPose(out uint cell, out _, out _, out _, out float qw, out _, out _, out float qz) || cell == 0)
            return (false, 0f);
        double yaw = 2.0 * Math.Atan2(qz, qw) * (180.0 / Math.PI);
        return (true, (float)((-yaw % 360.0 + 720.0) % 360.0));
    }

    /// <summary>The game through RynthNet's host (plugin tick).</summary>
    private sealed class HostGame : ICharacterGame
    {
        private readonly RynthNetPlugin _p;
        public HostGame(RynthNetPlugin p) => _p = p;
        private RynthCoreHost Host => _p.Host;

        public uint PlayerId => Host.GetPlayerId();
        public bool InWorld => _p._loggedIn;
        public bool Portaling => Host.HasIsPortaling && Host.IsPortaling();
        public uint SelectedId => Host.GetSelectedItemId();

        public bool TryGetPose(out uint cell, out float x, out float y, out float z) =>
            Host.TryGetPlayerPose(out cell, out x, out y, out z, out _, out _, out _, out _);

        public bool TryGetVitals(out uint hp, out uint hpMax, out uint st, out uint stMax, out uint mp, out uint mpMax) =>
            Host.TryGetPlayerVitals(out hp, out hpMax, out st, out stMax, out mp, out mpMax);

        public bool TryGetName(uint id, out string name) => Host.TryGetObjectName(id, out name);

        public bool TryGetPosition(uint id, out uint cell, out float x, out float y, out float z)
        {
            cell = 0; x = y = z = 0;
            return Host.HasGetObjectPosition && Host.TryGetObjectPosition(id, out cell, out x, out y, out z);
        }
    }
}

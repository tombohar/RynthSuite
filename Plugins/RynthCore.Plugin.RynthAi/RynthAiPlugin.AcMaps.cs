// RynthAiPlugin.AcMaps.cs — the ACMAPS Maps window and /ra acmaps (2026-11-26, a player's request:
// "Is there a way to display these ACMAPS maps in game? Like GoArrow used to?").
//
// ACMAPS' hand-drawn dungeon maps (rooms, quest items, levers, portals) are the player's own: RynthSuite
// doesn't ship them. The player points the window at their map pack (Folder > Browse..., or the default
// <RynthSuite>\RynthAi\ACMaps); AcMaps/AcMapIndex.cs reads it (images named by landblock, dungeons.txt,
// a GoArrow locations file). In a dungeon with a map, the window shows it, and the player's arrow once
// the floor is placed (AcMaps/AcMapCalibration.cs: "Place me here", saved per map in acmaps.json).
//
// Ways in, buttons first: the RynthAi window's Map button, right-click; the ⋯ menu > ACMAPS maps; and
// /ra acmaps. The window is a plugin window (API v89) declared without a bar button (the bar is full).
// The engine's Dungeon Map is RynthCore's panel and stays as it is (the layer rule: plugins own their
// screens), so this is a window of RynthAi's own beside it.
//
// Optional (off by default): a chat line as you enter a dungeon your folder has a map for.
//
// Threads: the commands and the chat line on the pump thread; the window on the render thread; the
// folder scan, the image decode and the settings write on threads of their own. The settings object is
// locked for every change and the write.
using System;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using ImGuiNET;
using RynthCore.Plugin.RynthAi.AcMaps;
using RynthCore.PluginSdk;

namespace RynthCore.Plugin.RynthAi;

public sealed partial class RynthAiPlugin : IAcMapsHost
{
    /// <summary>The window's title (its panel key) and surface (Huds/SectionsManifest.cs).</summary>
    internal const string AcMapsTitle = "ACMAPS Maps", AcMapsSurface = "window:acmaps";

    internal const string AcMapsUsage = "Usage: /ra acmaps [folder <path> | scan | announce on|off | hide]";

    /// <summary>The window's textures go after it hasn't been drawn this long (they're remade when it shows).</summary>
    private const long AcMapsIdleReleaseMs = 20000;

    private AcMapSettings? _acSettings;                       // made on first use, then locked for changes
    private readonly object _acSettingsGate = new();
    private volatile AcMapIndex _acIndex = AcMapIndex.Empty;
    private int _acScanning;                                  // 1 while a scan runs
    private int _acScanGeneration;
    private bool _acScannedOnce;
    private int _acSaveQueued;
    private AcMapWindow? _acWindow;                            // render thread
    private long _acDrawnMs = long.MinValue / 2;               // render thread writes, render reads
    private int _acFailures;                                   // render thread
    private ushort _acLastLandblock;                           // pump thread (the chat line)

    /// <summary>The window goes in the manifest on an engine with plugin windows (v89).</summary>
    private bool AcMapsWindowWanted => Host.HasUiTheme && Host.HasSetPanelOpen;

    /// <summary>Where the settings are written; tests point it at a scratch file.</summary>
    internal string AcMapsSettingsPath { get; set; } = AcMapSettings.DefaultPath;

    // ── IAcMapsHost (the window) ─────────────────────────────────────────

    AcMapIndex IAcMapsHost.Index => _acIndex;
    bool IAcMapsHost.Scanning => Volatile.Read(ref _acScanning) != 0;
    AcMapSettings IAcMapsHost.Settings => AcSettings;
    IntPtr IAcMapsHost.Device => Host.D3DDevice;
    void IAcMapsHost.SetFolder(string folder) => SetAcMapsFolder(folder);
    void IAcMapsHost.Rescan() => StartAcScan();
    void IAcMapsHost.SetAnnounce(bool on) => SetAcMapsAnnounce(on);
    void IAcMapsHost.SaveSettings() => SaveAcSettings();

    bool IAcMapsHost.TryPose(out uint cell, out float x, out float y, out float z, out float heading)
    {
        cell = 0; x = y = z = heading = 0;
        if (!Host.HasGetPlayerPose || !Host.TryGetPlayerPose(out cell, out x, out y, out z, out float qw, out _, out _, out float qz))
            return false;
        double yaw = 2.0 * Math.Atan2(qz, qw) * (180.0 / Math.PI);
        heading = (float)((-yaw % 360.0 + 720.0) % 360.0);
        return true;
    }

    /// <summary>The settings, read from acmaps.json the first time. Any thread.</summary>
    internal AcMapSettings AcSettings
    {
        get
        {
            AcMapSettings? s = Volatile.Read(ref _acSettings);
            if (s != null) return s;
            lock (_acSettingsGate)
            {
                _acSettings ??= AcMapSettings.Load(AcMapsSettingsPath);
                return _acSettings;
            }
        }
    }

    /// <summary>The current index (tests).</summary>
    internal AcMapIndex AcMapsIndexForTests => _acIndex;

    // ── Folder, scan, save ───────────────────────────────────────────────

    /// <summary>Reads the folder on a thread of its own; the window shows "Reading..." meanwhile. Any thread.</summary>
    internal Task StartAcScan()
    {
        _acScannedOnce = true;
        string folder;
        AcMapSettings s = AcSettings;
        lock (s) folder = s.FolderOrDefault;
        int gen = Interlocked.Increment(ref _acScanGeneration);
        Interlocked.Exchange(ref _acScanning, 1);
        return Task.Run(() =>
        {
            try
            {
                // The default folder is made, so the player has somewhere to copy a pack to.
                if (string.Equals(folder, AcMapSettings.DefaultFolder, StringComparison.OrdinalIgnoreCase))
                {
                    try { System.IO.Directory.CreateDirectory(folder); } catch { }
                }
                AcMapIndex index = AcMapIndex.Scan(folder);
                if (gen == Volatile.Read(ref _acScanGeneration)) _acIndex = index;
            }
            catch (Exception ex) { RynthLog.Exception(LogCat.UI, ex, "ACMAPS scan"); }
            finally
            {
                if (gen == Volatile.Read(ref _acScanGeneration)) Interlocked.Exchange(ref _acScanning, 0);
            }
        });
    }

    internal Task SetAcMapsFolder(string folder)
    {
        AcMapSettings s = AcSettings;
        lock (s) s.Folder = string.Equals(folder, AcMapSettings.DefaultFolder, StringComparison.OrdinalIgnoreCase) ? string.Empty : folder;
        SaveAcSettings();
        return StartAcScan();
    }

    internal void SetAcMapsAnnounce(bool on)
    {
        AcMapSettings s = AcSettings;
        lock (s) s.Announce = on;
        SaveAcSettings();
        if (on && !_acScannedOnce) StartAcScan();
    }

    /// <summary>Writes acmaps.json soon, off the calling thread; several changes in a row make one write.</summary>
    internal void SaveAcSettings()
    {
        if (Interlocked.Exchange(ref _acSaveQueued, 1) != 0) return;
        Task.Run(async () =>
        {
            await Task.Delay(300).ConfigureAwait(false);
            Interlocked.Exchange(ref _acSaveQueued, 0);
            AcMapSettings s = AcSettings;
            string json;
            lock (s) json = s.ToJson();
            string error = AcMapSettings.Save(AcMapsSettingsPath, json);
            if (error.Length > 0) Host.Log("[RynthAi] ACMAPS: couldn't save " + AcMapsSettingsPath + ": " + error);
        });
    }

    // ── /ra acmaps ───────────────────────────────────────────────────────

    /// <summary>
    /// "/ra acmaps" opens the window and says what's there; "folder &lt;path&gt;" sets the maps folder and reads
    /// it; "scan" reads it again; "announce on|off" the chat line; "hide" closes the window. Every form replies.
    /// Pump thread.
    /// </summary>
    private void HandleAcMapsCommand(string[] parts)
    {
        string verb = parts.Length > 2 ? parts[2].Trim().ToLowerInvariant() : string.Empty;
        string rest = parts.Length > 3 ? string.Join(" ", parts, 3, parts.Length - 3).Trim().Trim('"') : string.Empty;
        AcMapSettings s = AcSettings;
        switch (verb)
        {
            case "":
            case "show":
            case "open":
            {
                if (!_acScannedOnce) StartAcScan();
                int open = OpenAcMapsWindow(RynthPanelOpen.Open);
                ReplyOk("[RynthAi] ACMAPS maps: " + AcMapsHereLine() + AcWindowNote(open));
                return;
            }
            case "hide":
            case "close":
            {
                if (!Host.HasSetPanelOpen) { ReplyFail("[RynthAi] ACMAPS maps: this RynthCore has no plugin windows (plugin API 89+)."); return; }
                int open = Host.SetPanelOpen(AcMapsTitle, RynthPanelOpen.Close);
                if (open < 0) ReplyFail("[RynthAi] The ACMAPS Maps window isn't there yet (RynthCore adds it a few seconds after login).");
                else ReplyOk("[RynthAi] ACMAPS Maps window closed.");
                return;
            }
            case "folder":
            {
                if (rest.Length == 0)
                {
                    string f;
                    lock (s) f = s.FolderOrDefault;
                    ReplyOk("[RynthAi] ACMAPS maps folder: " + f + " (/ra acmaps folder <path> to change it).");
                    return;
                }
                if (!System.IO.Path.IsPathRooted(rest))
                {
                    ReplyFail($"[RynthAi] ACMAPS maps: '{rest}' isn't a full folder path (like C:\\ACMaps). {AcMapsUsage}");
                    return;
                }
                if (!System.IO.Directory.Exists(rest))
                {
                    ReplyFail($"[RynthAi] ACMAPS maps: there's no folder {rest}. {AcMapsUsage}");
                    return;
                }
                SetAcMapsFolder(rest);
                ReplyOk("[RynthAi] ACMAPS maps folder set to " + rest + ": reading it (the ACMAPS Maps window shows the count).");
                return;
            }
            case "scan":
            case "rescan":
            {
                string f;
                lock (s) f = s.FolderOrDefault;
                StartAcScan();
                ReplyOk("[RynthAi] ACMAPS maps: reading " + f + " again.");
                return;
            }
            case "announce":
            {
                bool? on = rest switch { "on" or "true" or "1" => true, "off" or "false" or "0" => false, "" => null, _ => null };
                if (on == null && rest.Length > 0) { ReplyFail($"[RynthAi] ACMAPS maps: announce takes on or off, not '{rest}'. {AcMapsUsage}"); return; }
                bool now;
                lock (s) now = on ?? !s.Announce;
                SetAcMapsAnnounce(now);
                ReplyOk(now ? "[RynthAi] ACMAPS maps: a line in chat when a dungeon has a map: ON."
                            : "[RynthAi] ACMAPS maps: the chat line when a dungeon has a map: OFF.");
                return;
            }
            case "help":
            case "?":
                ChatLine("[RynthAi] /ra acmaps — opens the ACMAPS Maps window: your own ACMAPS map of the dungeon you're in (also: RynthAi window > Map, right-click)");
                ChatLine("[RynthAi] /ra acmaps folder <path> | scan | announce on|off | hide — the maps folder, read it again, the chat line, close the window");
                return;
            default:
                ReplyFail($"[RynthAi] ACMAPS maps: unknown option '{verb}'. {AcMapsUsage}");
                return;
        }
    }

    /// <summary>"123 maps in C:\..; this dungeon (Mosswart Maze, 02CA) has one" for the command's reply.</summary>
    private string AcMapsHereLine()
    {
        AcMapIndex index = _acIndex;
        string folder;
        AcMapSettings s = AcSettings;
        lock (s) folder = s.FolderOrDefault;
        if (Volatile.Read(ref _acScanning) != 0 && index.Maps.Count == 0) return "reading " + folder + "...";
        string count = index.Maps.Count == 1 ? "1 map" : $"{index.Maps.Count} maps";
        string line = $"{count} in {folder}";
        ushort lb = CurrentDungeonLandblock();
        if (lb != 0)
        {
            string name = index.NameOf(lb);
            string which = name.Length > 0 ? $"{name}, {lb:X4}" : lb.ToString("X4");
            line += index.Has(lb) ? $"; this dungeon ({which}) has one" : $"; none for this dungeon ({which})";
        }
        return line;
    }

    private string AcWindowNote(int open) =>
        open == -2 ? " (the window needs RynthCore with plugin windows)"
        : open < 0 ? " (the window isn't there yet: RynthCore adds it a few seconds after login)"
        : " (ACMAPS Maps open).";

    /// <summary>The window opened, closed or toggled: 1 open, 0 closed, -1 not there yet, -2 no plugin windows. Any thread.</summary>
    private int OpenAcMapsWindow(RynthPanelOpen mode)
    {
        if (!Host.HasSetPanelOpen) return -2;
        int r = Host.SetPanelOpen(AcMapsTitle, mode);
        return r < 0 ? -1 : r;
    }

    /// <summary>The dashboard's Map (right-click) and ⋯ > ACMAPS maps: the window toggled. Tick.</summary>
    internal void ToggleAcMapsWindow()
    {
        if (!_acScannedOnce) StartAcScan();
        int r = OpenAcMapsWindow(RynthPanelOpen.Toggle);
        if (r == -1) ChatLine("[RynthAi] ACMAPS Maps isn't there yet (RynthCore adds it a few seconds after login). Try again in a moment, or type /ra acmaps.");
        else if (r == -2) ChatLine("[RynthAi] ACMAPS Maps needs a RynthCore with plugin windows (plugin API 89+).");
    }

    /// <summary>The landblock of the dungeon you're in (0 outdoors or before login).</summary>
    private ushort CurrentDungeonLandblock()
    {
        if (!Host.HasGetPlayerPose || !Host.TryGetPlayerPose(out uint cell, out _, out _, out _, out _, out _, out _, out _)) return 0;
        return (cell & 0xFFFF) >= 0x100 ? (ushort)(cell >> 16) : (ushort)0;
    }

    // ── The chat line (pump thread) ──────────────────────────────────────

    /// <summary>With Announce on: a line as you enter a dungeon that has a map. Pump thread, every tick (cheap).</summary>
    private void TickAcMaps()
    {
        if (!_loginComplete) return;
        AcMapSettings? s = Volatile.Read(ref _acSettings);
        if (s == null)
        {
            // Read the settings once after login (a small file), so Announce works without the window.
            s = AcSettings;
        }
        bool announce;
        lock (s) announce = s.Announce;
        if (!announce) { _acLastLandblock = 0; return; }
        if (!_acScannedOnce) StartAcScan();
        ushort lb = CurrentDungeonLandblock();
        if (lb == _acLastLandblock) return;
        AcMapIndex index = _acIndex;
        if (lb != 0 && Volatile.Read(ref _acScanning) != 0 && index.Maps.Count == 0) return;   // ask again when the scan is done
        _acLastLandblock = lb;
        string? line = AcAnnounceLine(index, lb);
        if (line != null) Host.WriteToChat(line, CommandReply.OkColor);
    }

    /// <summary>The chat line for entering <paramref name="landblock"/>, or null (outdoors, or no map). Pure.</summary>
    internal static string? AcAnnounceLine(AcMapIndex index, ushort landblock)
    {
        if (landblock == 0 || !index.Has(landblock)) return null;
        string name = index.NameOf(landblock);
        if (name.Length == 0) name = index.ForLandblock(landblock)[0].Label;
        return $"[RynthAi] ACMAPS has a map of {name}: RynthAi window > Map (right-click), or /ra acmaps.";
    }

    // ── Window (render thread) ───────────────────────────────────────────

    /// <summary>
    /// RynthPluginRenderEmbed "window:acmaps": the ACMAPS Maps window body, docked or popped out.
    /// 1 drawn, 0 not ready, -2 failed (three draws in a row threw).
    /// </summary>
    internal int RenderAcMaps(bool popped, out Vector2 want)
    {
        want = default;
        if (!_initialized || Host.ImGuiContext == IntPtr.Zero) return 0;
        _acDrawnMs = Environment.TickCount64;
        if (!_acScannedOnce) StartAcScan();
        IntPtr previous = ImGui.GetCurrentContext();
        ImGui.SetCurrentContext(EmbedContext());
        try
        {
            _acWindow ??= new AcMapWindow(this);
            want = _acWindow.Draw();
            _acFailures = 0;
            return 1;
        }
        catch (Exception ex)
        {
            RynthLog.Exception(LogCat.UI, ex, "RenderAcMaps");
            return ++_acFailures >= 3 ? -2 : 0;
        }
        finally
        {
            ImGui.SetCurrentContext(previous);
        }
    }

    /// <summary>Render thread, every frame: a closed window's map textures go after a while.</summary>
    private void ReleaseIdleAcMapTextures()
    {
        if (_acWindow != null && Environment.TickCount64 - _acDrawnMs > AcMapsIdleReleaseMs) _acWindow.ReleaseTextures();
    }
}

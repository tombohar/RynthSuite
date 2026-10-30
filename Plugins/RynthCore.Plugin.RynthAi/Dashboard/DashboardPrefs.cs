// ============================================================================
//  RynthAi - Dashboard/DashboardPrefs.cs
//  The dashboard window's own choices: minimized, the target bar, the vitals, the
//  open drawers (one per side) and the drawers moved to the side they don't start
//  on. One file for the PC, like the engine's dashboard state was (every client
//  shows the dashboard the same way): %AppData%\RynthCore\rynthai_dashboard.txt,
//  "key=value" lines. The first time, it starts from the engine dashboard's file
//  (rynthai_avalonia.cache: line 1 minimized, 3 the target bar, 4 the vitals, 12
//  the moved drawers), so the switch to RynthAi's own window keeps them.
//  The engine's file also says which look is on (line 14, classic|modern): the
//  Classic look is still RynthCore's face, so RynthAi's window steps aside for it
//  (ClassicLook).
//
//  The window sets the fields on the render thread; the plugin tick saves them
//  (SaveIfChanged), never the render thread.
// ============================================================================

using System;
using System.IO;
using System.Linq;

namespace RynthCore.Plugin.RynthAi.Dashboard;

internal sealed class DashboardPrefs
{
    // The drawers, in the order their tabs stack (the engine dashboard's keys, so its moves carry over).
    public const string RangesDrawer = "ranges", FilesDrawer = "files", PatrolDrawer = "patrol",
        RemoteDrawer = "remote", PinnedDrawer = "quick";
    /// <summary>Every drawer key; My settings (quick) starts on the right, the rest on the left.</summary>
    public static readonly string[] Drawers = { RangesDrawer, FilesDrawer, PatrolDrawer, RemoteDrawer, PinnedDrawer };
    public static bool StartsRight(string key) => key == PinnedDrawer;

    private static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RynthCore");
    private static string FilePath => Path.Combine(Folder, "rynthai_dashboard.txt");
    /// <summary>The engine dashboard's state (RynthCore UI/RynthAiDashboardState.cs).</summary>
    private static string EngineStatePath => Path.Combine(Folder, "rynthai_avalonia.cache");

    private volatile bool _minimized, _showTarget = true, _showVitals = true;
    private volatile string _left = string.Empty, _right = string.Empty;
    private volatile string[] _moved = Array.Empty<string>();
    private volatile bool _dirty;

    public bool Minimized => _minimized;
    public bool ShowTarget => _showTarget;
    public bool ShowVitals => _showVitals;

    public void SetMinimized(bool v) { _minimized = v; _dirty = true; }
    public void SetShowTarget(bool v) { _showTarget = v; _dirty = true; }
    public void SetShowVitals(bool v) { _showVitals = v; _dirty = true; }

    /// <summary>The drawer is on the right edge now (its start side, or the other one when moved).</summary>
    public bool OnRight(string key) => StartsRight(key) != (Array.IndexOf(_moved, key) >= 0);

    /// <summary>The drawer open on that side, or "".</summary>
    public string OpenOn(bool right) => right ? _right : _left;

    public bool IsOpen(string key) => (OnRight(key) ? _right : _left) == key;

    /// <summary>Opens <paramref name="key"/>'s drawer (closing the one open on its side), or closes it when it is open.</summary>
    public void ToggleDrawer(string key)
    {
        if (OnRight(key)) _right = _right == key ? string.Empty : key;
        else _left = _left == key ? string.Empty : key;
        _dirty = true;
    }

    public void CloseDrawer(string key)
    {
        if (_left == key) _left = string.Empty;
        if (_right == key) _right = string.Empty;
        _dirty = true;
    }

    /// <summary>Moves the drawer to the other edge; open, it opens there (closing the one open there).</summary>
    public void MoveDrawer(string key)
    {
        bool wasOpen = IsOpen(key);
        CloseDrawer(key);
        _moved = Array.IndexOf(_moved, key) >= 0 ? _moved.Where(k => k != key).ToArray() : _moved.Append(key).ToArray();
        if (wasOpen) ToggleDrawer(key);
        _dirty = true;
    }

    private static bool IsDrawer(string key) => Array.IndexOf(Drawers, key) >= 0;

    /// <summary>Reads the file, or starts from the engine dashboard's state. Plugin init.</summary>
    public static DashboardPrefs Load()
    {
        var p = new DashboardPrefs();
        try
        {
            string[] engine = File.Exists(EngineStatePath) ? File.ReadAllLines(EngineStatePath) : Array.Empty<string>();
            bool movedRead = false;
            if (File.Exists(FilePath))
            {
                string legacyDrawer = string.Empty;
                foreach (string raw in File.ReadAllLines(FilePath))
                {
                    int eq = raw.IndexOf('=');
                    if (eq <= 0) continue;
                    string key = raw[..eq].Trim(), value = raw[(eq + 1)..].Trim();
                    bool on = value == "1";
                    switch (key)
                    {
                        case "minimized": p._minimized = on; break;
                        case "target": p._showTarget = on; break;
                        case "vitals": p._showVitals = on; break;
                        case "left": p._left = IsDrawer(value) ? value : string.Empty; break;
                        case "right": p._right = IsDrawer(value) ? value : string.Empty; break;
                        case "drawer": legacyDrawer = value; break;   // before 2026-10-21's two sides
                        case "moved":
                            movedRead = true;
                            p._moved = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(IsDrawer).ToArray();
                            break;
                    }
                }
                if (!movedRead) p._moved = EngineMoved(engine);
                if (legacyDrawer.Length > 0 && IsDrawer(legacyDrawer) && p.OpenOn(p.OnRight(legacyDrawer)).Length == 0)
                    p.ToggleDrawer(legacyDrawer);
                p._dirty = !movedRead || legacyDrawer.Length > 0;
                return p;
            }
            // First time: the engine dashboard's choices, so nothing changes at the switch.
            if (engine.Length > 0) p._minimized = engine[0].Trim() == "minimized";
            if (engine.Length > 2) p._showTarget = engine[2].Trim() != "notarget";
            if (engine.Length > 3) p._showVitals = engine[3].Trim() != "novitals";
            p._moved = EngineMoved(engine);
            p._dirty = true;
        }
        catch { /* unreadable: the defaults */ }
        return p;
    }

    /// <summary>The engine dashboard's moved drawers (line 12, "moved:files,patrol").</summary>
    private static string[] EngineMoved(string[] engine)
    {
        if (engine.Length < 12) return Array.Empty<string>();
        string line = engine[11].Trim();
        if (!line.StartsWith("moved:", StringComparison.OrdinalIgnoreCase)) return Array.Empty<string>();
        return line[6..].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(IsDrawer).ToArray();
    }

    /// <summary>Writes the file when something changed. Plugin tick.</summary>
    public void SaveIfChanged()
    {
        if (!_dirty) return;
        _dirty = false;
        try
        {
            Directory.CreateDirectory(Folder);
            File.WriteAllText(FilePath,
                "minimized=" + (_minimized ? 1 : 0) + "\n" +
                "target=" + (_showTarget ? 1 : 0) + "\n" +
                "vitals=" + (_showVitals ? 1 : 0) + "\n" +
                "left=" + _left + "\n" +
                "right=" + _right + "\n" +
                "moved=" + string.Join(",", _moved) + "\n");
        }
        catch { /* best effort: the next change tries again */ }
    }

    /// <summary>
    /// True while the engine dashboard's look is Classic (its file's line 14), the retail-style window
    /// RynthCore still draws: RynthAi's window then steps aside. Plugin tick (a small file read).
    /// </summary>
    public static bool ClassicLook()
    {
        try
        {
            if (!File.Exists(EngineStatePath)) return false;
            string[] lines = File.ReadAllLines(EngineStatePath);
            return lines.Length > 13 && lines[13].Trim().Equals("classic", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }
}

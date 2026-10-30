using System;
using System.Collections.Generic;
using System.IO;

namespace RynthCore.Plugin.RynthDefective.Model;

/// <summary>
/// Which keys the engine's hotkey system has bound to a chat command, read from its file
/// (%LOCALAPPDATA%\RynthCore\hotkeys.txt, one binding a line: "on|off TAB combo TAB command TAB text",
/// the action "command" for a chat command). Read only: the plugin adds and removes bindings by
/// typing "/rc hotkey add|remove ..." to the engine, never by writing the file. The file is
/// shared by every client on the PC and the engine picks up changes within 2 s, so a binding
/// shows here a moment after it was made.
/// </summary>
internal sealed class HotkeyLookup
{
    public const string EnvOverride = "RYNTHCORE_HOTKEYS_FILE";

    private readonly Func<string> _path;
    private DateTime _stamp = DateTime.MinValue;
    private long _nextCheckMs;
    private List<(string Combo, string Command, bool On)> _bindings = new();

    public HotkeyLookup() : this(DefaultPath) { }
    public HotkeyLookup(Func<string> path) => _path = path;

    public static string DefaultPath()
    {
        if (Environment.GetEnvironmentVariable(EnvOverride) is { Length: > 0 } p) return p;
        string root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(root, "RynthCore", "hotkeys.txt");
    }

    /// <summary>The combo bound to exactly this chat command ("/rdef use 3"), or null. Looks at the file at most once a second.</summary>
    public string? KeyFor(string command, long nowMs)
    {
        Refresh(nowMs);
        string want = Normalize(command);
        foreach (var b in _bindings)
            if (b.On && b.Command == want) return b.Combo;
        foreach (var b in _bindings)
            if (b.Command == want) return b.Combo + " (off)";
        return null;
    }

    /// <summary>Forget what was read: the next lookup reads the file again.</summary>
    public void Invalidate() { _stamp = DateTime.MinValue; _nextCheckMs = 0; }

    private void Refresh(long nowMs)
    {
        if (nowMs < _nextCheckMs) return;
        _nextCheckMs = nowMs + 1000;
        try
        {
            string path = _path();
            if (!File.Exists(path))
            {
                _bindings = new();
                _stamp = DateTime.MinValue;
                return;
            }
            DateTime stamp = File.GetLastWriteTimeUtc(path);
            if (stamp == _stamp) return;
            _stamp = stamp;
            _bindings = Parse(File.ReadAllText(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The engine is writing it right now: the old list stays, the next look tries again.
        }
    }

    internal static List<(string Combo, string Command, bool On)> Parse(string? text)
    {
        var list = new List<(string, string, bool)>();
        if (string.IsNullOrEmpty(text)) return list;
        foreach (string raw in text.Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            if (line.Trim().Length == 0 || line.TrimStart().StartsWith('#')) continue;
            string[] f = line.Split('\t', 4);
            if (f.Length < 4) continue;
            if (!f[2].Trim().Equals("command", StringComparison.OrdinalIgnoreCase)) continue;
            string combo = f[1].Trim();
            if (combo.Length == 0) continue;
            list.Add((combo, Normalize(f[3]), !f[0].Trim().Equals("off", StringComparison.OrdinalIgnoreCase)));
        }
        return list;
    }

    private static string Normalize(string command) =>
        string.Join(' ', (command ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant();
}

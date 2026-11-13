// ============================================================================
//  RynthAi - TimedCommands/TimedCommands.cs
//  Timed commands (2026-11-03, players asked for MagTools' periodic commands):
//  chat commands RynthAi sends every N seconds, in three lists as MagTools
//  keeps them: this character, every character on this server, and every
//  character. A character runs all three.
//
//  Files (beside the settings profiles, so switching a settings profile keeps
//  them): SettingsProfiles\<server>\<character>\timed_commands.json,
//  SettingsProfiles\<server>\timed_commands.json and
//  SettingsProfiles\timed_commands.json.
//
//  Each command: the text (sent as a script's line: a /ra, /mt, /ub or /rc
//  command, an emote, a channel...), every how many seconds (1 to 86400), the
//  first run how many seconds after login (0 = one interval), on/off, and
//  "only while the macro runs". Rules (TimedCommandScheduler): at most one command a second,
//  none before login completes, while in portal space or before the first
//  delay; a command that came due while it couldn't run goes once when it can
//  (no backlog), then every interval from then.
//
//  Pure (no host): the host tests drive it.
// ============================================================================
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;

namespace RynthCore.Plugin.RynthAi.TimedCommands;

/// <summary>Which list a timed command is in.</summary>
internal enum TimedScope { Character = 0, Server = 1, All = 2 }

/// <summary>One timed command (edited by the window and /ra timed; run by TimedCommandScheduler).</summary>
internal sealed class TimedCommand
{
    public const int MinSeconds = 1, MaxSeconds = 86400, DefaultSeconds = 600;
    public const int MaxLength = 200;

    public string Command = string.Empty;
    /// <summary>Every how many seconds (1 to 86400).</summary>
    public int Seconds = DefaultSeconds;
    /// <summary>Seconds after login before the first run (0 to 86400); 0 = one interval.</summary>
    public int FirstAfter;
    public bool Enabled = true;
    /// <summary>Only while the macro runs (it waits while the macro is stopped).</summary>
    public bool MacroOnly;

    /// <summary>Environment.TickCount64 when it is due next (0 = not scheduled yet). Not saved.</summary>
    internal long NextDueMs;

    /// <summary>An interval in whole seconds, 1 to 86400 (not a number: the default 600).</summary>
    public static int ClampSeconds(double s) =>
        double.IsNaN(s) || double.IsInfinity(s) ? DefaultSeconds : (int)Math.Clamp(Math.Round(s), MinSeconds, MaxSeconds);
    /// <summary>A first delay in whole seconds, 0 to 86400 (negative or not a number: 0).</summary>
    public static int ClampFirstAfter(double s) =>
        double.IsNaN(s) || double.IsInfinity(s) || s < 0 ? 0 : (int)Math.Min(Math.Round(s), MaxSeconds);
    public static string CleanCommand(string? text)
    {
        string t = (text ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ').Trim();
        return t.Length > MaxLength ? t[..MaxLength] : t;
    }

    /// <summary>"every 30 s" / "every 600 s (10 min)".</summary>
    public string EveryText() => Every(Seconds);

    public static string Every(int seconds) => "every " + Duration(seconds);

    /// <summary>"30 s", "90 s (1 min 30 s)", "600 s (10 min)", "86400 s (24 h)".</summary>
    public static string Duration(int seconds)
    {
        string s = seconds.ToString(CultureInfo.InvariantCulture) + " s";
        if (seconds < 60) return s;
        int h = seconds / 3600, m = seconds / 60 % 60, rest = seconds % 60;
        var parts = new List<string>(3);
        if (h > 0) parts.Add(h.ToString(CultureInfo.InvariantCulture) + " h");
        if (m > 0) parts.Add(m.ToString(CultureInfo.InvariantCulture) + " min");
        if (rest > 0) parts.Add(rest.ToString(CultureInfo.InvariantCulture) + " s");
        return s + " (" + string.Join(' ', parts) + ")";
    }
}

/// <summary>The three lists and their files.</summary>
internal sealed class TimedCommandSet
{
    public const string FileName = "timed_commands.json";

    /// <summary>Locks every read and edit of the lists (the window is on the render thread, the scheduler on the tick).</summary>
    public readonly object Sync = new();
    private readonly List<TimedCommand>[] _lists = { new(), new(), new() };
    private readonly string[] _paths = { string.Empty, string.Empty, string.Empty };
    private readonly bool[] _dirty = new bool[3];

    /// <summary>The character and server the lists are for (folder names; empty before login).</summary>
    public string Character { get; private set; } = string.Empty;
    public string Server { get; private set; } = string.Empty;
    public bool Loaded { get; private set; }

    /// <summary>Changed since the last save (any list). Under <see cref="Sync"/> or from the tick.</summary>
    public long ChangedAtMs { get; private set; }

    /// <summary>The list for <paramref name="scope"/>; lock <see cref="Sync"/> around any use.</summary>
    public List<TimedCommand> List(TimedScope scope) => _lists[(int)scope];

    public static string ScopeName(TimedScope s) => s switch
    {
        TimedScope.Character => "this character",
        TimedScope.Server => "this server",
        _ => "all characters",
    };

    /// <summary>
    /// Loads the three lists for the character whose settings folder is <paramref name="charFolder"/>
    /// (SettingsProfiles\&lt;server&gt;\&lt;character&gt;). Problems go to <paramref name="problems"/>; a missing file is an empty list.
    /// </summary>
    public void Load(string charFolder, List<string>? problems = null)
    {
        string? serverFolder = Path.GetDirectoryName(charFolder);
        string? baseFolder = serverFolder != null ? Path.GetDirectoryName(serverFolder) : null;
        lock (Sync)
        {
            Character = Path.GetFileName(charFolder);
            Server = serverFolder != null ? Path.GetFileName(serverFolder) : string.Empty;
            _paths[(int)TimedScope.Character] = Path.Combine(charFolder, FileName);
            _paths[(int)TimedScope.Server] = serverFolder != null ? Path.Combine(serverFolder, FileName) : string.Empty;
            _paths[(int)TimedScope.All] = baseFolder != null ? Path.Combine(baseFolder, FileName) : string.Empty;
            for (int i = 0; i < 3; i++)
            {
                _lists[i].Clear();
                _dirty[i] = false;
                if (_paths[i].Length == 0 || !File.Exists(_paths[i])) continue;
                try { _lists[i].AddRange(Parse(File.ReadAllText(_paths[i]))); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
                {
                    problems?.Add($"{ScopeName((TimedScope)i)} ({_paths[i]}): {ex.Message}");
                }
            }
            Loaded = true;
        }
    }

    /// <summary>Forgets the character (logout): nothing runs until the next load.</summary>
    public void Unload()
    {
        lock (Sync)
        {
            for (int i = 0; i < 3; i++) { _lists[i].Clear(); _paths[i] = string.Empty; _dirty[i] = false; }
            Character = Server = string.Empty;
            Loaded = false;
        }
    }

    /// <summary>A list was edited: saved by <see cref="SaveDirty"/>. Call under <see cref="Sync"/>.</summary>
    public void MarkChanged(TimedScope scope, long nowMs)
    {
        _dirty[(int)scope] = true;
        ChangedAtMs = nowMs;
    }

    public bool AnyDirty { get { lock (Sync) return _dirty[0] || _dirty[1] || _dirty[2]; } }

    /// <summary>Writes every edited list (temp file, then replace). Problems go to <paramref name="problems"/>.</summary>
    public void SaveDirty(List<string>? problems = null)
    {
        lock (Sync)
        {
            for (int i = 0; i < 3; i++)
            {
                if (!_dirty[i] || _paths[i].Length == 0) continue;
                _dirty[i] = false;
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(_paths[i])!);
                    string tmp = _paths[i] + ".tmp";
                    File.WriteAllText(tmp, Serialize(_lists[i]));
                    File.Move(tmp, _paths[i], overwrite: true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    problems?.Add($"{ScopeName((TimedScope)i)} ({_paths[i]}): {ex.Message}");
                }
            }
        }
    }

    // ── File format: {"version":2,"commands":[{"command","seconds","firstAfterSeconds","enabled","macroOnly"}]} ──
    // Version 1 (before 2026-11-12) kept "minutes" and "firstAfter" in minutes: read and turned into
    // seconds (×60), so an old list keeps its timing; the next save writes version 2.

    public const int FileVersion = 2;

    public static string Serialize(IReadOnlyList<TimedCommand> list)
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            w.WriteNumber("version", FileVersion);
            w.WriteStartArray("commands");
            foreach (TimedCommand c in list)
            {
                w.WriteStartObject();
                w.WriteString("command", c.Command);
                w.WriteNumber("seconds", c.Seconds);
                w.WriteNumber("firstAfterSeconds", c.FirstAfter);
                w.WriteBoolean("enabled", c.Enabled);
                w.WriteBoolean("macroOnly", c.MacroOnly);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    public static List<TimedCommand> Parse(string json)
    {
        var list = new List<TimedCommand>();
        using JsonDocument doc = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("commands", out JsonElement arr)
            || arr.ValueKind != JsonValueKind.Array)
            return list;
        foreach (JsonElement e in arr.EnumerateArray())
        {
            if (e.ValueKind != JsonValueKind.Object) continue;
            var c = new TimedCommand
            {
                Command = TimedCommand.CleanCommand(e.TryGetProperty("command", out JsonElement t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null),
                Seconds = TimedCommand.ClampSeconds(
                    Number(e, "seconds", out double sec) ? sec
                    : Number(e, "minutes", out double min) ? min * 60
                    : TimedCommand.DefaultSeconds),
                FirstAfter = TimedCommand.ClampFirstAfter(
                    Number(e, "firstAfterSeconds", out double fsec) ? fsec
                    : Number(e, "firstAfter", out double fmin) ? fmin * 60
                    : 0),
                Enabled = !e.TryGetProperty("enabled", out JsonElement en) || en.ValueKind != JsonValueKind.False,
                MacroOnly = e.TryGetProperty("macroOnly", out JsonElement mo) && mo.ValueKind == JsonValueKind.True,
            };
            list.Add(c);
        }
        return list;
    }

    private static bool Number(JsonElement e, string name, out double value)
    {
        value = 0;
        return e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out value);
    }
}

/// <summary>What the scheduler needs to know about the client this tick.</summary>
internal readonly record struct TimedRunState(long NowMs, bool InWorld, bool Portaling, bool MacroRunning);

/// <summary>When each timed command runs (see the file header for the rules).</summary>
internal sealed class TimedCommandScheduler
{
    /// <summary>Two timed commands are never sent less than this apart.</summary>
    public const long GapMs = 1000;
    private long _lastSentMs = long.MinValue / 2;
    private long _loginMs;

    /// <summary>Login completed at <paramref name="nowMs"/>: every command starts its first delay from here.</summary>
    public void OnLogin(TimedCommandSet set, long nowMs)
    {
        _loginMs = nowMs;
        lock (set.Sync)
            for (int s = 0; s < 3; s++)
                foreach (TimedCommand c in set.List((TimedScope)s))
                    c.NextDueMs = c.Enabled && c.Command.Length > 0 ? FirstDue(c, nowMs) : 0;
    }

    /// <summary>The first run of <paramref name="c"/> after login (or after it was added or turned on, when that's later).</summary>
    public long FirstDue(TimedCommand c, long fromMs) =>
        Math.Max(_loginMs, fromMs) + (c.FirstAfter > 0 ? Math.Min(c.FirstAfter, TimedCommand.MaxSeconds) * 1000L : IntervalMs(c));

    /// <summary>
    /// The interval in ms. Clamped here as well: the window's box holds what is typed until it is left
    /// (typing "600" passes through "6" and an empty box is 0), and that must never mean "every tick".
    /// </summary>
    public static long IntervalMs(TimedCommand c) => TimedCommand.ClampSeconds(c.Seconds) * 1000L;

    /// <summary>
    /// The command to send now, if any (at most one per call, <see cref="GapMs"/> apart): of those due,
    /// the one that has waited longest. Schedules commands seen for the first time (added or turned on
    /// since login: their first delay starts now). Locks the set.
    /// </summary>
    public bool TryNext(TimedCommandSet set, in TimedRunState st, out string command, out TimedScope scope)
    {
        command = string.Empty;
        scope = TimedScope.Character;
        if (!set.Loaded) return false;
        lock (set.Sync)
        {
            TimedCommand? pick = null;
            for (int s = 0; s < 3; s++)
                foreach (TimedCommand c in set.List((TimedScope)s))
                {
                    if (!c.Enabled || c.Command.Length == 0) { c.NextDueMs = 0; continue; }
                    if (c.NextDueMs == 0) c.NextDueMs = FirstDue(c, st.NowMs);
                    if (st.NowMs >= c.NextDueMs && (!c.MacroOnly || st.MacroRunning) && (pick == null || c.NextDueMs < pick.NextDueMs))
                    {
                        pick = c;
                        scope = (TimedScope)s;
                    }
                }
            if (pick == null || !st.InWorld || st.Portaling || st.NowMs - _lastSentMs < GapMs) return false;
            _lastSentMs = st.NowMs;
            pick.NextDueMs = st.NowMs + IntervalMs(pick);
            command = pick.Command;
            return true;
        }
    }

    /// <summary>A command sent by hand (Run now): the next run is one interval from now.</summary>
    public void RanNow(TimedCommand c, long nowMs)
    {
        c.NextDueMs = nowMs + IntervalMs(c);
        _lastSentMs = nowMs;
    }

    /// <summary>"in 4:30" / "waiting for the macro" / "off" for the window and /ra timed list. Under the set's lock.</summary>
    public static string NextText(TimedCommand c, long nowMs, bool macroRunning)
    {
        if (!c.Enabled) return "off";
        if (c.Command.Length == 0) return "no command";
        if (c.MacroOnly && !macroRunning) return "waits for the macro";
        if (c.NextDueMs == 0) return "scheduling";
        long left = Math.Max(0, c.NextDueMs - nowMs) / 1000;
        return left >= 3600
            ? $"in {left / 3600}:{left / 60 % 60:00}:{left % 60:00}"
            : $"in {left / 60}:{left % 60:00}";
    }
}

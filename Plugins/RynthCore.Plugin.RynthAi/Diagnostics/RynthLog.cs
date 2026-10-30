using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using RynthCore.PluginSdk;
using RynthCore.Install;

namespace RynthCore.Plugin.RynthAi;

/// <summary>
/// One trace category per RynthAi function. Each category can be traced
/// independently (<c>/ra trace &lt;cat&gt; on</c>) into its own daily file under
/// <c>Logs\Diagnostics\Trace</c>, mirroring UtilityBelt's per-feature trace toggles.
/// </summary>
internal enum LogCat
{
    // ── Core / macro systems ───────────────────────────────────────────────
    General,
    Commands,
    Combat,
    Buffing,
    Pets,
    WorldCache,
    Navigation,
    Doors,
    Jumper,
    Looting,
    Inventory,
    Salvage,
    Vendor,
    ManaStones,
    Meta,
    Expressions,
    Quests,
    Crafting,
    Raycast,
    Radar,
    UI,
    Remote,
    Chat,
    Huds,
    // The ILT Hub's categories (IltHub, IltOptions, ...) went with it to the RynthILT plugin
    // (2026-10-13), which keeps its own log ("/ilt trace").
}

/// <summary>
/// Persisted diagnostics switches (<c>Logs\Diagnostics\diagnostics.json</c>).
/// Fields (not properties) so the source-generated <see cref="RynthAiJsonContext"/>
/// (IncludeFields) round-trips them without reflection under NativeAOT.
/// </summary>
internal sealed class RynthLogConfig
{
    /// <summary>Echo trace + exception lines to the in-game chat window (UB's "debug" switch).</summary>
    public bool DebugToChat;

    /// <summary>Mirror every <see cref="RynthLog.Write"/> line into the daily RynthAi log file.</summary>
    public bool FileLogAll = true;

    /// <summary>
    /// Category names (see <see cref="LogCat"/>) with tracing enabled. Kept in sync with
    /// <see cref="Categories"/> (every category at Trace or Info); only read when
    /// <see cref="Categories"/> is empty (diagnostics.json written by an older RynthAi).
    /// </summary>
    public List<string> Traces = new();

    /// <summary>
    /// Per-category trace level (Off / Trace / Info) for every <see cref="LogCat"/>. Rewritten on
    /// every load with the full category list and descriptions so the launcher can edit it.
    /// </summary>
    public List<LogCategorySetting> Categories = new();

    /// <summary>Delete diagnostics files older than this many days.</summary>
    public int RetainDays = 7;

    /// <summary>Roll a log file to a new numbered part once it exceeds this size (MB).</summary>
    public int MaxFileMb = 10;

    /// <summary>Keep at most this many files per diagnostics folder (oldest pruned first).</summary>
    public int MaxFiles = 30;

    /// <summary>
    /// Per-event log levels for the named key events in <see cref="LogEvents"/>. Rewritten on every
    /// load with the full catalog (new events at their defaults, fresh descriptions), so the launcher
    /// can list and edit every event without its own copy of the catalog.
    /// </summary>
    public List<LogEventSetting> Events = new();
}

/// <summary>Where a named key event is written (see <see cref="RynthLog.Event"/>).</summary>
internal enum LogEventLevel
{
    /// <summary>Not recorded at all.</summary>
    Off = 0,
    /// <summary>Only in the category trace file, and only while that category is traced.</summary>
    Trace = 1,
    /// <summary>Normal log: engine log + daily RynthAi file (+ trace file when traced).</summary>
    Info = 2,
}

/// <summary>
/// One persisted key-event switch in <c>diagnostics.json</c>. Only <see cref="Level"/> is read back;
/// the other fields are informational for the launcher and are refreshed from the catalog on load.
/// </summary>
internal sealed class LogEventSetting
{
    /// <summary>Stable event id, e.g. "Area.Event".</summary>
    public string Key = string.Empty;

    /// <summary>"Info", "Trace" or "Off" (case-insensitive; anything else falls back to the default).</summary>
    public string Level = nameof(LogEventLevel.Info);

    /// <summary>Default level for this event (informational).</summary>
    public string Default = nameof(LogEventLevel.Info);

    /// <summary>Owning <see cref="LogCat"/> name (informational; picks the trace file).</summary>
    public string Category = string.Empty;

    /// <summary>Human-readable description shown by the launcher.</summary>
    public string Description = string.Empty;
}

/// <summary>
/// One persisted category trace switch in <c>diagnostics.json</c>. <see cref="Level"/> decides where
/// the category's <see cref="RynthLog.Trace"/> lines go: Off → dropped, Trace → the category trace
/// file, Info → the trace file AND the normal log. Normal <see cref="RynthLog.Write"/> lines are
/// unaffected. Only <see cref="Level"/> is read back.
/// </summary>
internal sealed class LogCategorySetting
{
    /// <summary><see cref="LogCat"/> name, e.g. "Combat".</summary>
    public string Name = string.Empty;

    /// <summary>"Off", "Trace" or "Info" (case-insensitive; anything else reads as Off).</summary>
    public string Level = nameof(LogEventLevel.Off);

    /// <summary>Human-readable description shown by the launcher.</summary>
    public string Description = string.Empty;
}

/// <summary>
/// Catalog of named key events. Each one has its own configurable level, so a milestone like
/// "Hub login" can show in the normal log while the surrounding detail stays trace-only.
/// Add new events here; the launcher picks them up from <c>diagnostics.json</c> automatically.
/// </summary>
internal static class LogEvents
{
    // The ILT Hub's events (IltHub.Login, ...) went with it to the RynthILT plugin (2026-10-13);
    // none of RynthAi's own are cataloged yet.

    /// <summary>Catalog entry: key, owning category, default level, launcher description.</summary>
    internal readonly record struct Definition(string Key, LogCat Category, LogEventLevel Default, string Description);

    /// <summary>Every known event, in launcher display order.</summary>
    internal static readonly Definition[] Catalog =
    {
    };

    /// <summary>Launcher description for each trace category.</summary>
    internal static string DescribeCategory(LogCat cat) => cat switch
    {
        LogCat.General => "Uncategorised RynthAi lines.",
        LogCat.Commands => "/ra chat commands and remote commands.",
        LogCat.Combat => "Combat: targeting, attacks, spells, wield gates.",
        LogCat.Buffing => "Buffing: buff timers, rebuff decisions, casts.",
        LogCat.Pets => "Pet summoning and upkeep.",
        LogCat.WorldCache => "World object cache: classification and ownership.",
        LogCat.Navigation => "Navigation: routes, waypoints, movement state.",
        LogCat.Doors => "Door detection and opening.",
        LogCat.Jumper => "Jump commands.",
        LogCat.Looting => "Corpse looting and loot rules.",
        LogCat.Inventory => "Inventory: AutoStack, AutoCram, equip.",
        LogCat.Salvage => "Salvage combining and use.",
        LogCat.Vendor => "AutoVendor buy/sell.",
        LogCat.ManaStones => "Mana stone use and recharging.",
        LogCat.Meta => "Meta (VTank-style) state machine and actions.",
        LogCat.Expressions => "Meta expression evaluation.",
        LogCat.Quests => "Quest tracking.",
        LogCat.Crafting => "Missile ammo crafting and the Mini Remote's Crafting tab.",
        LogCat.Raycast => "Line-of-sight raycasts and geometry loading.",
        LogCat.Radar => "Radar window.",
        LogCat.UI => "RynthAi UI windows and panels.",
        LogCat.Remote => "Remote control (launcher/Avalonia commands).",
        LogCat.Chat => "Chat parsing and chat diagnostics.",
        LogCat.Huds => "Floating HUDs: item counts, Mini Remote, icons.",
        _ => cat.ToString(),
    };
}

/// <summary>
/// Central RynthAi logging / debugging facility, modelled on UtilityBelt's Logger:
/// <list type="bullet">
///   <item><see cref="Write"/> — normal log line: engine log (as before) + daily <c>rynthai_*.txt</c>
///         + the category trace file when that category is being traced.</item>
///   <item><see cref="Trace"/> — verbose line, only recorded when the category is traced; optionally
///         echoed to chat when <see cref="DebugToChat"/> is on.</item>
///   <item><see cref="Exception"/> — full stack trace into <c>exceptions_*.txt</c> with per-signature
///         throttling so a per-frame fault can't fill the disk.</item>
/// </list>
/// Thread-safe: callable from the pump thread, the ImGui render thread and background tasks.
/// Chat echo is queued and drained on the pump thread by <see cref="Pump"/>.
/// </summary>
internal static class RynthLog
{
    /// <summary>Default diagnostics root (inside the RynthAi data folder the installer creates).</summary>
    public static readonly string DefaultDirectory = System.IO.Path.Combine(RynthInstallPaths.RynthAiDir, @"Logs\Diagnostics");

    // ── Exception throttling (same budgets as UB's Logger) ────────────────
    private static readonly TimeSpan ExceptionBurstWindow = TimeSpan.FromSeconds(5);
    private const int MaxExceptionsPerSignaturePerDay = 25;
    private const int MaxExceptionsPerDay = 500;

    /// <summary>Upper bound on chat echo lines drained per tick (prevents chat floods).</summary>
    private const int MaxChatLinesPerPump = 20;

    /// <summary>Upper bound on queued chat echo lines; extra lines are dropped (files still get them).</summary>
    private const int MaxQueuedChatLines = 200;

    private static readonly object FileLock = new();
    private static readonly object ConfigLock = new();
    private static readonly Dictionary<string, StreamWriter> Writers = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, int> RollParts = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, (DateTime lastAt, int count)> ExceptionSignatures = new();
    private static readonly ConcurrentQueue<string> ChatQueue = new();

    // Per-category trace level as (int)LogEventLevel, indexed by (int)LogCat. Volatile reads are
    // enough — changes are rare, reads are hot. Off = not traced, Trace = trace file, Info = trace
    // file + normal log.
    private static readonly int[] CategoryLevels = new int[Enum.GetValues<LogCat>().Length];

    // Key-event levels and categories. Replaced wholesale on load (never mutated after publish),
    // so hot-path readers on any thread can use them without a lock.
    private static volatile Dictionary<string, LogEventLevel> _eventLevels = BuildDefaultEventLevels();
    private static readonly Dictionary<string, LogCat> EventCategories = BuildEventCategories();

    /// <summary>How often <see cref="Pump"/> checks diagnostics.json for edits made by the launcher.</summary>
    private const long ConfigPollIntervalMs = 2000;
    private static long _nextConfigPollAt;
    private static DateTime _configStampUtc = DateTime.MinValue;

    private static RynthCoreHost? _host;
    private static RynthLogConfig _config = new();
    private static string _directory = DefaultDirectory;
    private static string _openDay = string.Empty;
    private static int _exceptionsToday;
    // The process-wide exception hooks. Kept so Shutdown can take them off again: AppDomain and
    // TaskScheduler live in the default context, so a handler left there holds this plugin's
    // collectible context, and through it the engine generation, after a hot reload.
    private static UnhandledExceptionEventHandler? _unhandledHandler;
    private static EventHandler<UnobservedTaskExceptionEventArgs>? _unobservedHandler;
    private static bool _dirty;

    /// <summary>Folder holding <c>rynthai_*.txt</c>, <c>exceptions_*.txt</c> and <c>diagnostics.json</c>.</summary>
    public static string Directory => _directory;

    /// <summary>Folder holding per-category <c>{Category}_*.txt</c> trace files.</summary>
    public static string TraceDirectory => Path.Combine(_directory, "Trace");

    /// <summary>When true, trace and exception lines are echoed to chat (UB's global debug).</summary>
    public static bool DebugToChat
    {
        get => _config.DebugToChat;
        set { lock (ConfigLock) { SyncFromDiskIfChanged(); _config.DebugToChat = value; SaveConfig(); } }
    }

    /// <summary>When true, every <see cref="Write"/> line is mirrored to the daily RynthAi log file.</summary>
    public static bool FileLogAll
    {
        get => _config.FileLogAll;
        set { lock (ConfigLock) { SyncFromDiskIfChanged(); _config.FileLogAll = value; SaveConfig(); } }
    }

    /// <summary>
    /// Binds the logger to the plugin host, loads persisted switches, prunes old files and
    /// installs the global unhandled-exception hooks. Safe to call more than once.
    /// </summary>
    public static void Init(RynthCoreHost host, string? directory = null)
    {
        _host = host;
        _directory = string.IsNullOrWhiteSpace(directory) ? DefaultDirectory : directory;

        try
        {
            System.IO.Directory.CreateDirectory(_directory);
            System.IO.Directory.CreateDirectory(TraceDirectory);
        }
        catch { /* folder is created lazily on first write as a fallback */ }

        // Write the merged catalog back when it changed so the launcher can list every event.
        if (LoadConfig()) SaveConfig();
        Prune();
        RegisterGlobalHandlers();
    }

    // ═════════════════════════════════════════════════════════════════════
    //  Public logging API
    // ═════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Normal log line. Always goes to the engine log (unchanged behaviour), plus the daily
    /// RynthAi file and — if the category is traced — the category's trace file.
    /// <see cref="LogCat.General"/> lines are re-routed by their "[Prefix]" when one is recognised.
    /// </summary>
    public static void Write(LogCat cat, string message)
    {
        if (string.IsNullOrEmpty(message)) return;
        if (cat == LogCat.General) cat = CategoryFromPrefix(message);

        try { _host?.Log(message); } catch { /* host gone during unload */ }

        if (_config.FileLogAll)
            AppendLine(DailyPath(_directory, "rynthai"), $"[{cat}] {message}");

        if (IsTracing(cat))
            AppendLine(DailyPath(TraceDirectory, cat.ToString()), "INFO  " + message);
    }

    /// <summary>
    /// Verbose diagnostic line. Recorded only when <paramref name="cat"/> is traced, so call
    /// sites can trace freely; guard expensive message building with <see cref="IsTracing"/>.
    /// When the category's level is Info the line is also promoted to the normal log
    /// (engine log + daily RynthAi file).
    /// </summary>
    public static void Trace(LogCat cat, string message)
    {
        if (!IsTracing(cat) || string.IsNullOrEmpty(message)) return;

        AppendLine(DailyPath(TraceDirectory, cat.ToString()), "TRACE " + message);
        if (GetCategoryLevel(cat) == LogEventLevel.Info)
        {
            try { _host?.Log($"[RynthAi:{cat}] {message}"); } catch { /* host gone during unload */ }
            if (_config.FileLogAll)
                AppendLine(DailyPath(_directory, "rynthai"), $"[{cat}] TRACE {message}");
        }
        if (_config.DebugToChat) QueueChat($"[RynthAi:{cat}] {message}");
    }

    /// <summary>
    /// Records an exception with its full stack trace. A one-line summary always goes to the
    /// engine log; the detailed entry is throttled per signature (5 s burst window,
    /// 25/signature/day, 500/day total) exactly like UB's Logger.LogException.
    /// </summary>
    public static void Exception(LogCat cat, Exception ex, string context = "")
    {
        if (ex == null) return;

        string where = string.IsNullOrEmpty(context) ? cat.ToString() : $"{cat}/{context}";
        string summary = $"[RynthAi] {where} error: {ex.GetType().Name}: {ex.Message}";
        try { _host?.Log(summary); } catch { }

        if (!ShouldRecordException(where, ex, out int repeat)) return;

        var sb = new StringBuilder();
        sb.Append("==== ").Append(where);
        if (repeat > 1) sb.Append(" (occurrence ").Append(repeat).Append(" today)");
        sb.AppendLine(" ====");
        sb.Append(ex);
        AppendLine(DailyPath(_directory, "exceptions"), sb.ToString(), flush: true);

        if (IsTracing(cat))
            AppendLine(DailyPath(TraceDirectory, cat.ToString()), "ERROR " + summary);
        if (_config.DebugToChat) QueueChat(summary);
    }

    /// <summary>
    /// Named key event (see <see cref="LogEvents"/>). Its configured level decides where it goes:
    /// Info → <see cref="Write"/> (normal log), Trace → <see cref="Trace"/>, Off → dropped.
    /// Levels are set per event in <c>diagnostics.json</c>, editable from the launcher.
    /// </summary>
    public static void Event(string eventKey, string message)
    {
        if (string.IsNullOrEmpty(message)) return;
        LogCat cat = EventCategories.TryGetValue(eventKey, out LogCat c) ? c : LogCat.General;
        string line = $"[{eventKey}] {message}";
        switch (GetEventLevel(eventKey))
        {
            case LogEventLevel.Info: Write(cat, line); break;
            case LogEventLevel.Trace: Trace(cat, line); break;
        }
    }

    /// <summary>Configured level for an event; unknown keys default to Info so nothing is silently lost.</summary>
    public static LogEventLevel GetEventLevel(string eventKey)
        => _eventLevels.TryGetValue(eventKey, out LogEventLevel l) ? l : LogEventLevel.Info;

    /// <summary>
    /// True when <see cref="Event"/> would record anything for this key right now. Use it to skip
    /// building an expensive message.
    /// </summary>
    public static bool IsEventEnabled(string eventKey)
    {
        LogEventLevel level = GetEventLevel(eventKey);
        if (level == LogEventLevel.Info) return true;
        return level == LogEventLevel.Trace
            && EventCategories.TryGetValue(eventKey, out LogCat cat) && IsTracing(cat);
    }

    /// <summary>True when the category is currently being traced (level Trace or Info; cheap; safe on hot paths).</summary>
    public static bool IsTracing(LogCat cat) => GetCategoryLevel(cat) != LogEventLevel.Off;

    /// <summary>Current trace level of a category (Off, Trace or Info).</summary>
    public static LogEventLevel GetCategoryLevel(LogCat cat)
    {
        int i = (int)cat;
        return (uint)i < (uint)CategoryLevels.Length
            ? (LogEventLevel)System.Threading.Volatile.Read(ref CategoryLevels[i])
            : LogEventLevel.Off;
    }

    /// <summary>Sets a category's trace level and persists the change.</summary>
    public static void SetCategoryLevel(LogCat cat, LogEventLevel level)
    {
        int i = (int)cat;
        if ((uint)i >= (uint)CategoryLevels.Length) return;
        lock (ConfigLock)
        {
            SyncFromDiskIfChanged();
            System.Threading.Volatile.Write(ref CategoryLevels[i], (int)level);
            SaveConfig();
        }
    }

    /// <summary>
    /// Enables/disables tracing for a single category and persists the change. Enabling keeps a
    /// category that is already at Info (promoted to the normal log) at Info.
    /// </summary>
    public static void SetTracing(LogCat cat, bool enabled)
    {
        int i = (int)cat;
        if ((uint)i >= (uint)CategoryLevels.Length) return;
        lock (ConfigLock)
        {
            SyncFromDiskIfChanged();
            ApplyTracing(i, enabled);
            SaveConfig();
        }
    }

    /// <summary>Enables/disables tracing for every category whose name starts with <paramref name="prefix"/> (empty = all).</summary>
    public static int SetTracingByPrefix(string prefix, bool enabled)
    {
        int changed = 0;
        lock (ConfigLock)
        {
            SyncFromDiskIfChanged();
            foreach (LogCat cat in Enum.GetValues<LogCat>())
            {
                if (prefix.Length > 0 && !cat.ToString().StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                ApplyTracing((int)cat, enabled);
                changed++;
            }
            SaveConfig();
        }
        return changed;
    }

    /// <summary>On → Trace unless already traced; off → Off.</summary>
    private static void ApplyTracing(int index, bool enabled)
    {
        int current = System.Threading.Volatile.Read(ref CategoryLevels[index]);
        int next = !enabled ? (int)LogEventLevel.Off
            : current == (int)LogEventLevel.Off ? (int)LogEventLevel.Trace : current;
        System.Threading.Volatile.Write(ref CategoryLevels[index], next);
    }

    /// <summary>
    /// Every category as "Name=Level" joined by ',', in enum order (Level: 0 Off, 1 Trace, 2 Info).
    /// The engine Settings panel's Diagnostics tab draws its checkbox grid from this.
    /// </summary>
    public static string FormatCategoryLevels()
    {
        var sb = new StringBuilder();
        foreach (LogCat cat in Enum.GetValues<LogCat>())
        {
            if (sb.Length > 0) sb.Append(',');
            sb.Append(cat).Append('=').Append((int)GetCategoryLevel(cat));
        }
        return sb.ToString();
    }

    /// <summary>
    /// Applies a <see cref="FormatCategoryLevels"/> string. Unknown names and bad levels are
    /// skipped, categories not listed keep their level, and diagnostics.json is written once,
    /// only when something changed. Returns the number of categories changed.
    /// </summary>
    public static int ApplyCategoryLevels(string? levels)
    {
        if (string.IsNullOrWhiteSpace(levels)) return 0;
        int changed = 0;
        lock (ConfigLock)
        {
            SyncFromDiskIfChanged();
            foreach (string entry in levels.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                int eq = entry.IndexOf('=');
                if (eq <= 0 || !TryParseCategory(entry[..eq], out LogCat cat)) continue;
                if (!int.TryParse(entry[(eq + 1)..], out int level) || !Enum.IsDefined((LogEventLevel)level)) continue;
                int i = (int)cat;
                if (System.Threading.Volatile.Read(ref CategoryLevels[i]) == level) continue;
                System.Threading.Volatile.Write(ref CategoryLevels[i], level);
                changed++;
            }
            if (changed > 0) SaveConfig();
        }
        return changed;
    }

    /// <summary>Parses a category name case-insensitively (e.g. "ilthub", "Combat").</summary>
    public static bool TryParseCategory(string name, out LogCat cat)
        => Enum.TryParse(name, ignoreCase: true, out cat) && Enum.IsDefined(cat);

    /// <summary>All categories currently being traced.</summary>
    public static List<LogCat> TracedCategories()
    {
        var list = new List<LogCat>();
        foreach (LogCat cat in Enum.GetValues<LogCat>())
            if (IsTracing(cat)) list.Add(cat);
        return list;
    }

    // ═════════════════════════════════════════════════════════════════════
    //  Lifecycle
    // ═════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Pump-thread housekeeping, called once per OnTick: drains queued chat echo lines and
    /// flushes buffered file writes so a crash loses at most one tick of log output.
    /// </summary>
    public static void Pump()
    {
        // RynthCoreHost is a struct; unwrap the nullable copy before calling into it.
        if (!ChatQueue.IsEmpty && _host is RynthCoreHost host)
        {
            for (int n = 0; n < MaxChatLinesPerPump && ChatQueue.TryDequeue(out string? line); n++)
            {
                try { if (host.HasWriteToChat) host.WriteToChat(line, 1); } catch { }
            }
        }

        if (_dirty) Flush();

        // Pick up event-level / trace edits the launcher wrote while the game is running.
        long now = Environment.TickCount64;
        if (now >= _nextConfigPollAt)
        {
            _nextConfigPollAt = now + ConfigPollIntervalMs;
            ReloadConfigIfChangedOnDisk();
        }
    }

    /// <summary>Reloads diagnostics.json when its timestamp differs from our last load/save.</summary>
    private static void ReloadConfigIfChangedOnDisk()
    {
        try
        {
            if (!File.Exists(ConfigPath)) return;
            if (File.GetLastWriteTimeUtc(ConfigPath) == _configStampUtc) return;
            lock (ConfigLock)
            {
                if (LoadConfig()) SaveConfig();
            }
            try { _host?.Log("[RynthAi] diagnostics.json changed on disk - category and event log levels reloaded."); } catch { }
        }
        catch
        {
            // A half-written file is retried on the next poll.
        }
    }

    /// <summary>Flushes every open log writer to disk.</summary>
    public static void Flush()
    {
        lock (FileLock)
        {
            foreach (var w in Writers.Values)
            {
                try { w.Flush(); } catch { }
            }
            _dirty = false;
        }
    }

    /// <summary>Flushes and closes every log file (plugin unload / logout).</summary>
    public static void Shutdown()
    {
        UnregisterGlobalHandlers();
        lock (FileLock)
        {
            foreach (var w in Writers.Values)
            {
                try { w.Flush(); w.Dispose(); } catch { }
            }
            Writers.Clear();
            _dirty = false;
        }
    }

    /// <summary>
    /// Deletes diagnostics files older than <see cref="RynthLogConfig.RetainDays"/> and trims each
    /// folder to <see cref="RynthLogConfig.MaxFiles"/>. Only touches <c>*.txt</c> files under the
    /// diagnostics folders, never other RynthAi data.
    /// </summary>
    public static int Prune()
    {
        int removed = 0;
        lock (FileLock)
        {
            removed += PruneFolder(_directory);
            removed += PruneFolder(TraceDirectory);
        }
        return removed;
    }

    /// <summary>Human-readable status lines for <c>/ra trace status</c> and <c>/ra logs</c>.</summary>
    public static IEnumerable<string> StatusLines()
    {
        var traced = TracedCategories();
        yield return $"[RynthAi] Debug-to-chat: {(DebugToChat ? "ON" : "off")}   File log: {(FileLogAll ? "ON" : "off")}";
        yield return traced.Count == 0
            ? "[RynthAi] Tracing: (none)"
            : "[RynthAi] Tracing: " + string.Join(", ", traced);
        yield return $"[RynthAi] Logs: {_directory}  (retain {_config.RetainDays} d, roll at {_config.MaxFileMb} MB)";
    }

    // ═════════════════════════════════════════════════════════════════════
    //  Internals
    // ═════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Maps the "[Prefix]" convention used by existing plugin log lines onto a category so
    /// that, e.g., "[BuffDiag] …" lands in the Buffing trace without touching the call site.
    /// </summary>
    private static LogCat CategoryFromPrefix(string message)
    {
        if (message.Length < 3 || message[0] != '[') return LogCat.General;

        if (Starts(message, "[Buff")) return LogCat.Buffing;
        if (Starts(message, "[MetaCast") || Starts(message, "[Meta")) return LogCat.Meta;
        if (Starts(message, "[Combat") || Starts(message, "[WieldGate") || Starts(message, "[Hazard")
            || Starts(message, "[Kill") || Starts(message, "[ScanTele") || Starts(message, "[RynthAi combat"))
            return LogCat.Combat;
        if (Starts(message, "[Equip") || Starts(message, "[RynthAi dumpinv")) return LogCat.Inventory;
        if (Starts(message, "[LOS") || Starts(message, "[Raycast") || Starts(message, "[GeoLoader")) return LogCat.Raycast;
        if (Starts(message, "[Jumper")) return LogCat.Jumper;
        if (Starts(message, "[ChatDiag") || Starts(message, "[RynthChat")) return LogCat.Chat;
        if (Starts(message, "[Reclassify") || Starts(message, "[Classify")) return LogCat.WorldCache;
        if (Starts(message, "[RynthAi navstate") || Starts(message, "[Nav")) return LogCat.Navigation;
        return LogCat.General;
    }

    private static bool Starts(string s, string prefix) => s.StartsWith(prefix, StringComparison.Ordinal);

    /// <summary>Builds <c>{folder}\{stem}_yyyy-MM-dd.txt</c>, with a numbered suffix once the day's file has rolled.</summary>
    private static string DailyPath(string folder, string stem)
    {
        string key = folder + "|" + stem;
        string day = DateTime.Now.ToString("yyyy-MM-dd");
        int part;
        lock (FileLock) part = RollParts.TryGetValue(key + "|" + day, out int p) ? p : 0;
        string name = part == 0 ? $"{stem}_{day}.txt" : $"{stem}_{day}_{part}.txt";
        return Path.Combine(folder, name);
    }

    /// <summary>Appends a timestamped line, rolling the file when it passes the size cap.</summary>
    private static void AppendLine(string path, string text, bool flush = false)
    {
        try
        {
            lock (FileLock)
            {
                RollDayIfNeeded();

                if (!Writers.TryGetValue(path, out var writer))
                {
                    writer = OpenWriter(path);
                    Writers[path] = writer;
                }

                writer.Write(DateTime.Now.ToString("HH:mm:ss.fff"));
                writer.Write(' ');
                writer.WriteLine(text);
                _dirty = true;
                if (flush) writer.Flush();

                long cap = Math.Max(1, _config.MaxFileMb) * 1024L * 1024L;
                if (writer.BaseStream.Length > cap) Roll(path, writer);
            }
        }
        catch
        {
            // Logging must never throw into gameplay code (disk full, file locked, etc.).
        }
    }

    /// <summary>
    /// Opens the writer for <paramref name="path"/>. Each file has one writing process: two
    /// clients appending to one file each keep their own end-of-file offset and overwrite each
    /// other's lines. When another client already holds the file, this process writes
    /// <c>{name}.{pid}.txt</c> beside it instead (the engine's logs are per process the same way).
    /// </summary>
    private static StreamWriter OpenWriter(string path)
    {
        string folder = Path.GetDirectoryName(path)!;
        System.IO.Directory.CreateDirectory(folder);
        FileStream stream;
        try
        {
            // FileShare.Read: readers (the launcher, editors) are fine; a second writer is refused.
            stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
        }
        catch (IOException)
        {
            string own = Path.Combine(folder,
                $"{Path.GetFileNameWithoutExtension(path)}.{Environment.ProcessId}{Path.GetExtension(path)}");
            stream = new FileStream(own, FileMode.Append, FileAccess.Write, FileShare.Read);
        }
        return new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = false };
    }

    /// <summary>Closes an oversized file and bumps its part number so the next write opens a fresh file.</summary>
    private static void Roll(string path, StreamWriter writer)
    {
        try { writer.Flush(); writer.Dispose(); } catch { }
        Writers.Remove(path);

        string file = Path.GetFileNameWithoutExtension(path);
        string day = DateTime.Now.ToString("yyyy-MM-dd");
        int idx = file.IndexOf("_" + day, StringComparison.Ordinal);
        string stem = idx > 0 ? file[..idx] : file;
        string key = Path.GetDirectoryName(path) + "|" + stem + "|" + day;
        RollParts[key] = (RollParts.TryGetValue(key, out int p) ? p : 0) + 1;
    }

    /// <summary>Closes yesterday's writers at midnight so new lines go to the new day's files.</summary>
    private static void RollDayIfNeeded()
    {
        string day = DateTime.Now.ToString("yyyy-MM-dd");
        if (day == _openDay) return;

        foreach (var w in Writers.Values)
        {
            try { w.Flush(); w.Dispose(); } catch { }
        }
        Writers.Clear();
        RollParts.Clear();
        ExceptionSignatures.Clear();
        _exceptionsToday = 0;
        _openDay = day;
    }

    /// <summary>Per-signature exception throttle (UB budgets). Returns false when the entry should be skipped.</summary>
    private static bool ShouldRecordException(string where, Exception ex, out int occurrence)
    {
        occurrence = 0;
        lock (FileLock)
        {
            RollDayIfNeeded();
            if (_exceptionsToday >= MaxExceptionsPerDay) return false;

            string topFrame = ex.StackTrace?.Split('\n')[0].Trim() ?? string.Empty;
            string signature = $"{where}|{ex.GetType().FullName}|{topFrame}";
            DateTime now = RynthClock.Now;   // an interval (the burst window), not the time of day

            if (ExceptionSignatures.TryGetValue(signature, out var seen))
            {
                if (seen.count >= MaxExceptionsPerSignaturePerDay) return false;
                // Repeats inside the burst window are dropped: a per-frame fault yields one entry per window.
                if (now - seen.lastAt < ExceptionBurstWindow) return false;
                occurrence = seen.count + 1;
            }
            else
            {
                occurrence = 1;
            }

            ExceptionSignatures[signature] = (now, occurrence);
            _exceptionsToday++;
            return true;
        }
    }

    /// <summary>Hooks AppDomain / TaskScheduler so faults outside our try/catch blocks are still recorded.</summary>
    private static void RegisterGlobalHandlers()
    {
        if (_unhandledHandler != null) return;

        _unhandledHandler = (_, e) =>
        {
            if (e.ExceptionObject is Exception ex) Exception(LogCat.General, ex, "UnhandledException");
            Flush();
        };
        _unobservedHandler = (_, e) =>
        {
            Exception(LogCat.General, e.Exception, "UnobservedTaskException");
            e.SetObserved();
        };
        AppDomain.CurrentDomain.UnhandledException += _unhandledHandler;
        TaskScheduler.UnobservedTaskException += _unobservedHandler;
    }

    /// <summary>
    /// Takes the hooks off again (plugin shutdown). Without this every hot reload leaked the
    /// whole engine generation: the shim logged "Engine gen N NOT unloaded" (2026-10-12).
    /// </summary>
    private static void UnregisterGlobalHandlers()
    {
        if (_unhandledHandler != null) AppDomain.CurrentDomain.UnhandledException -= _unhandledHandler;
        if (_unobservedHandler != null) TaskScheduler.UnobservedTaskException -= _unobservedHandler;
        _unhandledHandler = null;
        _unobservedHandler = null;
    }

    /// <summary>Queues a chat echo line for the pump thread (WriteToChat is not render-thread safe).</summary>
    private static void QueueChat(string line)
    {
        if (ChatQueue.Count < MaxQueuedChatLines) ChatQueue.Enqueue(line);
    }

    /// <summary>Removes old/excess <c>*.txt</c> files from one diagnostics folder. Caller holds <see cref="FileLock"/>.</summary>
    private static int PruneFolder(string folder)
    {
        int removed = 0;
        try
        {
            if (!System.IO.Directory.Exists(folder)) return 0;

            var files = new List<FileInfo>(new DirectoryInfo(folder).GetFiles("*.txt"));
            files.Sort((a, b) => b.LastWriteTimeUtc.CompareTo(a.LastWriteTimeUtc)); // newest first
            DateTime cutoff = DateTime.UtcNow.AddDays(-Math.Max(1, _config.RetainDays));

            for (int i = 0; i < files.Count; i++)
            {
                var f = files[i];
                bool tooOld = f.LastWriteTimeUtc < cutoff;
                bool overCount = i >= Math.Max(5, _config.MaxFiles);
                if (!tooOld && !overCount) continue;
                if (Writers.ContainsKey(f.FullName)) continue; // never delete a file we're writing

                try { f.Delete(); removed++; } catch { }
            }
        }
        catch { }
        return removed;
    }

    private static string ConfigPath => Path.Combine(_directory, "diagnostics.json");

    /// <summary>
    /// Loads persisted switches; missing or corrupt files fall back to defaults. Merges the event
    /// catalog into <see cref="RynthLogConfig.Events"/> and returns true when that changed the
    /// config (new events, refreshed descriptions, dropped stale keys), so the caller can save it.
    /// </summary>
    private static bool LoadConfig()
    {
        lock (ConfigLock)
        {
            try
            {
                if (File.Exists(ConfigPath))
                {
                    _configStampUtc = File.GetLastWriteTimeUtc(ConfigPath);
                    string json = File.ReadAllText(ConfigPath);
                    _config = JsonSerializer.Deserialize(json, RynthAiJsonContext.Default.RynthLogConfig) ?? new RynthLogConfig();
                }
            }
            catch
            {
                _config = new RynthLogConfig();
            }

            _config.Traces ??= new List<string>();
            bool categoriesChanged = MergeCategoryLevels();
            bool eventsChanged = MergeEventCatalog();
            return categoriesChanged || eventsChanged;
        }
    }

    /// <summary>
    /// Publishes the per-category levels from <see cref="RynthLogConfig.Categories"/> (or, for a file
    /// from an older RynthAi with no Categories list, from <see cref="RynthLogConfig.Traces"/>) and
    /// rebuilds the Categories list in enum order with fresh descriptions. Returns true when the
    /// persisted list needs rewriting. Caller holds <see cref="ConfigLock"/>.
    /// </summary>
    private static bool MergeCategoryLevels()
    {
        var levels = new int[CategoryLevels.Length];
        bool fromCategories = _config.Categories != null && _config.Categories.Count > 0;

        if (fromCategories)
        {
            foreach (LogCategorySetting s in _config.Categories!)
            {
                if (s == null || !TryParseCategory(s.Name, out LogCat cat)) continue;
                if (Enum.TryParse(s.Level, ignoreCase: true, out LogEventLevel lvl) && Enum.IsDefined(lvl))
                    levels[(int)cat] = (int)lvl;
            }
        }
        else
        {
            foreach (string name in _config.Traces)
                if (TryParseCategory(name, out LogCat cat)) levels[(int)cat] = (int)LogEventLevel.Trace;
        }

        for (int i = 0; i < levels.Length; i++)
            System.Threading.Volatile.Write(ref CategoryLevels[i], levels[i]);

        List<LogCategorySetting> rows = BuildCategoryRows();
        bool changed = !fromCategories || _config.Categories!.Count != rows.Count;
        for (int i = 0; !changed && i < rows.Count; i++)
        {
            LogCategorySetting? old = _config.Categories![i];
            if (old == null || old.Name != rows[i].Name || old.Level != rows[i].Level || old.Description != rows[i].Description)
                changed = true;
        }
        _config.Categories = rows;
        return changed;
    }

    /// <summary>Snapshot of the live category levels as persisted rows, in enum order.</summary>
    private static List<LogCategorySetting> BuildCategoryRows()
    {
        var rows = new List<LogCategorySetting>(CategoryLevels.Length);
        foreach (LogCat cat in Enum.GetValues<LogCat>())
        {
            rows.Add(new LogCategorySetting
            {
                Name = cat.ToString(),
                Level = GetCategoryLevel(cat).ToString(),
                Description = LogEvents.DescribeCategory(cat),
            });
        }
        return rows;
    }

    /// <summary>
    /// Rebuilds <see cref="RynthLogConfig.Events"/> in catalog order, keeping each saved level and
    /// publishing the live level table. Returns true when the persisted list needs rewriting.
    /// </summary>
    private static bool MergeEventCatalog()
    {
        var saved = new Dictionary<string, LogEventSetting>(StringComparer.OrdinalIgnoreCase);
        foreach (LogEventSetting e in _config.Events ?? new List<LogEventSetting>())
            if (e != null && !string.IsNullOrEmpty(e.Key)) saved[e.Key] = e;

        bool changed = (_config.Events?.Count ?? 0) != LogEvents.Catalog.Length;
        var merged = new List<LogEventSetting>(LogEvents.Catalog.Length);
        var levels = new Dictionary<string, LogEventLevel>(StringComparer.OrdinalIgnoreCase);

        foreach (LogEvents.Definition def in LogEvents.Catalog)
        {
            LogEventLevel level = def.Default;
            if (saved.TryGetValue(def.Key, out LogEventSetting? s)
                && Enum.TryParse(s.Level, ignoreCase: true, out LogEventLevel parsed) && Enum.IsDefined(parsed))
                level = parsed;
            else
                changed = true; // new event, or an unreadable level reset to its default

            var row = new LogEventSetting
            {
                Key = def.Key,
                Level = level.ToString(),
                Default = def.Default.ToString(),
                Category = def.Category.ToString(),
                Description = def.Description,
            };
            if (s == null || s.Level != row.Level || s.Default != row.Default
                || s.Category != row.Category || s.Description != row.Description)
                changed = true;

            merged.Add(row);
            levels[def.Key] = level;
        }

        _config.Events = merged;
        _eventLevels = levels;
        return changed;
    }

    /// <summary>Default level table, used until diagnostics.json has been loaded.</summary>
    private static Dictionary<string, LogEventLevel> BuildDefaultEventLevels()
    {
        var d = new Dictionary<string, LogEventLevel>(StringComparer.OrdinalIgnoreCase);
        foreach (LogEvents.Definition def in LogEvents.Catalog) d[def.Key] = def.Default;
        return d;
    }

    /// <summary>Event key → owning category (fixed by the catalog).</summary>
    private static Dictionary<string, LogCat> BuildEventCategories()
    {
        var d = new Dictionary<string, LogCat>(StringComparer.OrdinalIgnoreCase);
        foreach (LogEvents.Definition def in LogEvents.Catalog) d[def.Key] = def.Category;
        return d;
    }

    /// <summary>
    /// Persists the current switches (trace set and category rows are rebuilt from the live levels).
    /// Mutators call <see cref="SyncFromDiskIfChanged"/> first, so a launcher edit made since our
    /// last load is adopted before this write rather than silently reverted.
    /// </summary>
    private static void SaveConfig()
    {
        // Toggles come from both the pump thread (/ra trace) and the render thread (Diagnostics tab).
        lock (ConfigLock)
        {
            try
            {
                _config.Traces = TracedCategories().ConvertAll(c => c.ToString());
                _config.Categories = BuildCategoryRows();
                System.IO.Directory.CreateDirectory(_directory);
                File.WriteAllText(ConfigPath, JsonSerializer.Serialize(_config, RynthAiJsonContext.Default.RynthLogConfig));
                _configStampUtc = File.GetLastWriteTimeUtc(ConfigPath);
            }
            catch { }
        }
    }

    /// <summary>
    /// Reloads diagnostics.json when the launcher has written it since our last load/save, so the
    /// mutation that follows starts from the launcher's values. Caller holds <see cref="ConfigLock"/>.
    /// </summary>
    private static void SyncFromDiskIfChanged()
    {
        try
        {
            if (File.Exists(ConfigPath) && File.GetLastWriteTimeUtc(ConfigPath) != _configStampUtc)
                LoadConfig();
        }
        catch
        {
            // Unreadable file: keep our in-memory switches.
        }
    }
}

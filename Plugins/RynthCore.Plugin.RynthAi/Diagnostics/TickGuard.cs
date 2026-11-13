using System;

namespace RynthCore.Plugin.RynthAi;

/// <summary>The steps of <see cref="RynthAiPlugin.OnTick"/> that each run under their own guard (<see cref="TickGuard"/>).</summary>
internal enum TickStep
{
    // Every tick, in world or not.
    MetaCommands,
    DataWriteProblems,
    CombatHook,
    DelayedCommands,
    FellowOpen,
    GameInput,
    EngineCommands,
    PropertyDump,
    PackSetup,
    SettingsPush,
    WorldCache,
    DifficultyTier,
    PendingStops,
    CreatureSave,
    AutoSave,
    MapBake,
    WatchedFiles,
    QuestTracker,
    LocalFeatures,
    RouteJobs,
    GiveQueue,
    ItemTool,
    TimedCommands,
    Jumper,
    Dashboard,
    // In world.
    VTankYield,
    VitalsRead,
    DeathCheck,
    LowComponents,
    Pacing,
    DeathHold,
    DeathRecall,
    HazardEscape,
    BusyTimeout,
    BuffsAndVitals,
    ReloadBlocker,
    PatrolReroute,
    AutoVendor,
    AutoTrade,
    WeaponElements,
    CramStack,
    InventoryStatus,
    CombatTelemetry,
    Salvage,
    ManaStones,
    Pets,
    MissileCrafting,
    Combat,
    Looting,
    Navigation,
    MtLoot,
    Meta,
}

/// <summary>
/// Keeps one OnTick step's exception from stopping the rest of the tick (structural audit
/// 2026-10-21, finding 4). OnTick had one try/catch round the whole tick, so a step that threw
/// every tick (bad spell data, a null after a reload) skipped everything after it, healing and
/// combat included, and all that showed was one log line every 10 s: "the bot just stood there".
/// Each step now runs in its own guard:
/// <list type="bullet">
/// <item>A fault is logged at most once every <see cref="LogEveryMs"/> per step, through
///   <see cref="RynthLog.Exception"/> (the summary line; the stack in exceptions_*.txt under its own
///   de-duplication), with the count since the last line. The rest of the tick runs.</item>
/// <item>A step that failed <see cref="TripAfter"/> calls in a row over at least
///   <see cref="TripAfterMs"/> is switched off, with a chat line, and tried again every
///   <see cref="RetryMs"/>; chat says when it works again.</item>
/// <item>Essential steps (healing and buffs, combat, movement and the keys, the death and hazard
///   holds, the busy watchdog, ...) are never switched off: one chat line says they keep failing,
///   and they are tried every tick, as before.</item>
/// </list>
/// Nothing changes while nothing throws. Pump thread only.
/// </summary>
internal sealed class TickGuard
{
    internal const int TripAfter = 30;
    internal const long TripAfterMs = 5_000;
    internal const long RetryMs = 60_000;
    internal const long LogEveryMs = 10_000;

    private readonly record struct StepInfo(string Name, LogCat Cat, bool Essential);

    // Indexed by TickStep.
    private static readonly StepInfo[] Steps = BuildSteps();

    private static StepInfo[] BuildSteps()
    {
        var s = new StepInfo[Enum.GetValues<TickStep>().Length];
        void Add(TickStep step, string name, LogCat cat, bool essential = false) => s[(int)step] = new(name, cat, essential);

        Add(TickStep.MetaCommands,      "meta edits",               LogCat.Meta);
        Add(TickStep.DataWriteProblems, "save problem report",      LogCat.General);
        Add(TickStep.CombatHook,        "script hook cast results", LogCat.Combat);
        Add(TickStep.DelayedCommands,   "delayed commands",         LogCat.Commands);
        Add(TickStep.FellowOpen,        "open fellowship on create", LogCat.Commands);
        Add(TickStep.GameInput,         "held keys",                LogCat.Commands, essential: true);
        Add(TickStep.EngineCommands,    "engine commands",          LogCat.Commands);
        Add(TickStep.PropertyDump,      "property dump",            LogCat.Commands);
        Add(TickStep.PackSetup,         "pack setup",               LogCat.Inventory);
        Add(TickStep.SettingsPush,      "settings to the engine",   LogCat.UI);
        Add(TickStep.WorldCache,        "world object cache",       LogCat.WorldCache, essential: true);
        Add(TickStep.DifficultyTier,    "difficulty tier",          LogCat.Combat);
        Add(TickStep.PendingStops,      "held stops",               LogCat.Navigation, essential: true);
        Add(TickStep.CreatureSave,      "creature data save",       LogCat.Combat);
        Add(TickStep.AutoSave,          "settings autosave",        LogCat.UI);
        Add(TickStep.MapBake,           "dungeon map bake",         LogCat.Navigation);
        Add(TickStep.WatchedFiles,      "monster and nav files",    LogCat.UI);
        Add(TickStep.QuestTracker,      "quest tracker",            LogCat.Quests);
        Add(TickStep.LocalFeatures,     "HUDs and panels",          LogCat.Huds);
        Add(TickStep.RouteJobs,         "dungeon route jobs",       LogCat.Navigation);
        Add(TickStep.GiveQueue,         "give queue",               LogCat.Inventory);
        Add(TickStep.ItemTool,          "item tool",                LogCat.Inventory);
        Add(TickStep.TimedCommands,     "timed commands",           LogCat.Commands);
        Add(TickStep.Jumper,            "jumper",                   LogCat.Jumper, essential: true);
        Add(TickStep.Dashboard,         "dashboard window",         LogCat.UI);

        Add(TickStep.VTankYield,        "VTank yield",              LogCat.General, essential: true);
        Add(TickStep.VitalsRead,        "vitals read",              LogCat.Buffing, essential: true);
        Add(TickStep.DeathCheck,        "death check",              LogCat.General, essential: true);
        Add(TickStep.LowComponents,     "low components check",     LogCat.Buffing);
        Add(TickStep.Pacing,            "action pacing",            LogCat.General, essential: true);
        Add(TickStep.DeathHold,         "death hold",               LogCat.General, essential: true);
        Add(TickStep.DeathRecall,       "recall on death",          LogCat.General, essential: true);
        Add(TickStep.HazardEscape,      "hazard escape",            LogCat.Navigation, essential: true);
        Add(TickStep.BusyTimeout,       "busy watchdog",            LogCat.General, essential: true);
        Add(TickStep.BuffsAndVitals,    "healing and buffs",        LogCat.Buffing, essential: true);
        Add(TickStep.ReloadBlocker,     "reload blocker",           LogCat.General);
        Add(TickStep.PatrolReroute,     "patrol reroute",           LogCat.Navigation);
        Add(TickStep.AutoVendor,        "AutoVendor",               LogCat.Vendor);
        Add(TickStep.AutoTrade,         "AutoTrade",                LogCat.Vendor);
        Add(TickStep.WeaponElements,    "weapon elements",          LogCat.Combat);
        Add(TickStep.CramStack,         "AutoCram/AutoStack",       LogCat.Inventory);
        Add(TickStep.InventoryStatus,   "inventory status",         LogCat.Inventory);
        Add(TickStep.CombatTelemetry,   "combat telemetry",         LogCat.Combat);
        Add(TickStep.Salvage,           "salvage",                  LogCat.Salvage);
        Add(TickStep.ManaStones,        "mana stones",              LogCat.ManaStones);
        Add(TickStep.Pets,              "pets",                     LogCat.Pets);
        Add(TickStep.MissileCrafting,   "missile crafting",         LogCat.Crafting);
        Add(TickStep.Combat,            "combat",                   LogCat.Combat, essential: true);
        Add(TickStep.Looting,           "looting",                  LogCat.Looting);
        Add(TickStep.Navigation,        "navigation",               LogCat.Navigation, essential: true);
        Add(TickStep.MtLoot,            "/mt loot",                 LogCat.Looting);
        Add(TickStep.Meta,              "meta",                     LogCat.Meta);
        return s;
    }

    private sealed class State
    {
        public int InARow;
        public long FirstFailAt;
        public long LastLogAt = long.MinValue / 2;
        public int SinceLog;
        public long Failures;
        public long Runs;
        public bool Off;
        public long RetryAt;
        public bool Warned;
    }

    private readonly State[] _state;
    private readonly Action<string> _log;
    private readonly Action<string> _chat;

    /// <summary>Clock (ms, monotonic); tests replace it.</summary>
    internal Func<long> Clock { get; set; } = static () => Environment.TickCount64;

    public TickGuard(Action<string> log, Action<string> chat)
    {
        _log = log;
        _chat = chat;
        _state = new State[Steps.Length];
        for (int i = 0; i < _state.Length; i++) _state[i] = new State();
    }

    internal static string NameOf(TickStep step) => Steps[(int)step].Name;
    internal static bool IsEssential(TickStep step) => Steps[(int)step].Essential;

    /// <summary>False while the step is switched off and its retry is not due.</summary>
    public bool MayRun(TickStep step)
    {
        State st = _state[(int)step];
        return !st.Off || Clock() >= st.RetryAt;
    }

    /// <summary>The step ran through.</summary>
    public void Succeeded(TickStep step)
    {
        State st = _state[(int)step];
        st.Runs++;
        if (st.Off)
        {
            st.Off = false;
            Say($"[RynthAi] {NameOf(step)} works again (it was off after failing every tick).");
        }
        else if (st.Warned)
        {
            _log($"[RynthAi] Tick step '{NameOf(step)}' runs through again after {st.InARow} failure(s) in a row.");
        }
        st.InARow = 0;
        st.Warned = false;
    }

    /// <summary>The step threw: log (throttled), and switch it off once it keeps failing.</summary>
    public void Failed(TickStep step, Exception ex)
    {
        StepInfo info = Steps[(int)step];
        State st = _state[(int)step];
        long now = Clock();
        st.Runs++;
        st.Failures++;
        st.SinceLog++;
        if (st.InARow++ == 0) st.FirstFailAt = now;

        if (now - st.LastLogAt >= LogEveryMs)
        {
            st.LastLogAt = now;
            // The summary line goes to the engine log; the stack to exceptions_*.txt (de-duplicated there).
            RynthLog.Exception(info.Cat, ex, "tick step " + info.Name);
            if (st.SinceLog > 1)
                _log($"[RynthAi] Tick step '{info.Name}' failed {st.SinceLog} time(s) since its last line ({st.InARow} in a row); the rest of the tick still runs.");
            st.SinceLog = 0;
        }

        if (st.Off)
        {
            // A retry that failed: off for another RetryMs, quietly (the log line above still counts it).
            st.RetryAt = now + RetryMs;
            return;
        }

        bool keepsFailing = st.InARow >= TripAfter && now - st.FirstFailAt >= TripAfterMs;
        if (!keepsFailing || st.Warned) return;
        st.Warned = true;
        string what = $"{ex.GetType().Name}: {ex.Message}";
        if (info.Essential)
        {
            Say($"[RynthAi] {info.Name} keeps failing ({what}); it is tried every tick, the rest of the bot keeps running. See the log.");
        }
        else
        {
            st.Off = true;
            st.RetryAt = now + RetryMs;
            Say($"[RynthAi] {info.Name} is off: it failed every tick for {(now - st.FirstFailAt) / 1000} s ({what}). The rest of the bot keeps running; it is tried again every {RetryMs / 1000} s.");
        }
    }

    private void Say(string line)
    {
        _log(line);
        try { _chat(line); } catch { /* chat gone during unload */ }
    }

    // ── Diagnostics and tests ─────────────────────────────────────────────

    internal bool IsOff(TickStep step) => _state[(int)step].Off;
    internal long Failures(TickStep step) => _state[(int)step].Failures;
    internal long Runs(TickStep step) => _state[(int)step].Runs;
    internal int FailuresInARow(TickStep step) => _state[(int)step].InARow;
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Text.RegularExpressions;
using RynthCore.Plugin.RynthAi;
using RynthCore.Plugin.RynthAi.CreatureData;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.PluginSdk;
using RynthCore.Install;

namespace RynthCore.Plugin.RynthAi.Meta;

internal sealed class MetaManager
{
    private readonly LegacyUiSettings _settings;
    private readonly RynthCoreHost _host;
    private readonly PlayerVitalsCache _vitals;
    private WorldObjectCache? _objectCache;
    private FellowshipTracker? _fellowshipTracker;
    private QuestTracker? _questTracker;
    private BuffManager? _buffManager;
    private uint _playerId;

    /// <summary>
    /// Callback for handling /mt (Mag-Tools) commands that need plugin-level access.
    /// Set by the plugin after construction via <see cref="SetMtCommandHandler"/>.
    /// </summary>
    private Func<string, bool>? _mtCommandHandler;
    private Action<string>? _raCommandHandler;

    // ── State tracking ───────────────────────────────────────────────────────
    private string _lastState = "";
    private DateTime _stateStartTime = RynthClock.Now;

    /// <summary>Wall clock for seconds-in-state, the watchdog and the chat window. Tests swap it
    /// for a fake clock; the plugin never changes it.</summary>
    internal Func<DateTime> Clock { get; set; } = static () => RynthClock.Now;
    private bool _lastMacroRunning;
    // PSecsInStateGE's clock: seconds spent in the current state. Restarts when the meta moves to
    // a different state or a meta is loaded; keeps running over a macro stop/start and a SetState
    // to the same state; pauses while the state has Called another and resumes on the Return.
    private DateTime _statePStartTime = RynthClock.Now;
    // Seconds the calling state had on its P clock, one entry per _stateStack entry (null when the
    // Return goes to a different state than the one that called).
    private readonly Stack<double?> _statePPaused = new();
    // Set by ReturnFromCall; applied when the tick sees the state change.
    private double? _statePResume;

    // Observability (GetStateSnapshot / §3.4). Written on the plugin-tick thread
    // inside the locked rule-eval pass; read by the bridge under the same lock.
    private string _lastFiredState = "", _lastFiredCond = "", _lastFiredAct = "";
    private DateTime _lastFiredAt = DateTime.MinValue;
    private string _lastExprError = "";
    private DateTime _lastExprErrorAt = DateTime.MinValue;
    private bool _viewsWarned;   // one-shot: VTank Views unsupported notice

    // Per-state rule index (§4). Rebuilt when the list ref/count or the
    // structural version (in-place rule edits) changes. Buckets are built in
    // list order so rule priority (first match wins) is unchanged.
    private readonly Dictionary<string, List<MetaRule>> _stateIndex = new(StringComparer.OrdinalIgnoreCase);
    private static readonly List<MetaRule> _emptyRules = new();
    private List<MetaRule>? _idxList;
    private int _idxCount = -1;
    private int _idxVersion = -1;

    // ── Chat tracking ────────────────────────────────────────────────────────
    // Every line from the last second is kept (it used to be one slot, so the
    // earlier lines of a burst were never checked). A rule consumes the line it
    // fired on, so re-entering its state can't fire it again on the same line.
    private readonly record struct ChatLine(long Seq, DateTime At, string Text, int Type);
    private readonly List<ChatLine> _chatLines = new();
    private long _chatSeq;
    private readonly Dictionary<MetaRule, long> _chatConsumed = new();
    private MetaRule? _evalRule;     // top-level rule being evaluated
    private long _evalChatSeq;       // newest chat line its condition matched
    private Match? _lastChatMatch;

    // ── Macro stop/start replay ──────────────────────────────────────────────
    // Starting the macro re-arms every rule of the state it is in (so a meta puts back a
    // route the player changed or cleared while it was stopped). The rules that had already
    // fired in that state before the stop "replay" when they fire again. A replayed nav load
    // (EmbedNav, /vt nav load, /ra nav load) of the route that is still loaded and unchanged
    // keeps the route where it was: its waypoint, a Linear route's direction, a finished
    // Once route finished; a replayed "/ra nav restart" is skipped. Reloading here restarted
    // Once routes on every stop/start (2026-10-28). Cleared by the next real state entry; a
    // rule that first fires after the start is not a replay, and its nav load starts the
    // route fresh as before.
    private readonly HashSet<MetaRule> _replayRules = new(ReferenceEqualityComparer.Instance);
    private bool _replaying;   // the action running now belongs to a replayed rule

    /// <summary>Where a meta's nav names are looked for as .nav files. Tests point it at a scratch folder.</summary>
    internal string NavFolder { get; set; } = Path.Combine(RynthCore.Install.RynthInstallPaths.RynthAiDir, "NavProfiles");

    // ── Call stack (CallMetaState / ReturnFromCall) ──────────────────────────
    private readonly Stack<string> _stateStack = new();

    /// <summary>
    /// Pushes the state a Return goes back to, with the calling state's P seconds when the Return
    /// goes back to that same state (so PSecsInStateGE resumes there instead of restarting).
    /// </summary>
    private void PushCall(string returnState)
    {
        _stateStack.Push(returnState);
        bool backHere = string.Equals(returnState, _settings.CurrentState, StringComparison.OrdinalIgnoreCase);
        _statePPaused.Push(backHere ? (Clock() - _statePStartTime).TotalSeconds : null);
    }

    // ── Watchdog ─────────────────────────────────────────────────────────────
    private bool _watchdogActive;
    private string _watchdogState = "";
    private float _watchdogMetersRequired;
    private double _watchdogSecondsConfig;
    private DateTime _watchdogExpiration;
    private Vector3 _lastWatchdogPos;

    // ── Portal state tracking ────────────────────────────────────────────────
    private bool _lastPortalState;
    private bool _portalEnteredThisTick;
    private bool _portalExitedThisTick;

    // ── Vendor state tracking ─────────────────────────────────────────────────
    private uint _openVendorId;
    private bool _vendorClosedThisTick;
    private bool _vendorClosedNow;   // the edge this tick's rule pass sees

    // ── Death (one-shot) ─────────────────────────────────────────────────────
    // The plugin holds every tick from a death until 5 s after the respawn, so
    // Think never runs while health reads 0. The death is latched here and the
    // first meta tick after the hold sees it.
    private bool _deathPending;
    private bool _deathNow;

    // ── Enchantment read buffers (reused per tick to avoid allocation) ────────
    private readonly uint[] _enchSpellIds = new uint[256];
    private readonly double[] _enchExpiryTimes = new double[256];

    // ── Expression engine ────────────────────────────────────────────────────
    private ExpressionEngine? _expressions;

    public MetaManager(LegacyUiSettings settings, RynthCoreHost host, PlayerVitalsCache vitals)
    {
        _settings = settings;
        _host = host;
        _vitals = vitals;
        // §3.3: surface schema/enum drift once per session instead of letting a
        // mid-enum insert silently corrupt every saved meta + the JSON bridge.
        if (MetaSchema.DriftError != null)
            RynthLog.Write(LogCat.Meta, $"[Meta] SCHEMA DRIFT — fix MetaSchema/enum alignment: {MetaSchema.DriftError}");
    }

    public void SetMtCommandHandler(Func<string, bool> handler) => _mtCommandHandler = handler;
    public void SetRaCommandHandler(Action<string> handler) => _raCommandHandler = handler;

    private CommandReply? _reply;

    /// <summary>What /vt commands say back (CommandReply.cs). The plugin hands over its own, so a /vt
    /// line answers like every other command; on its own (tests) a meta's action is the "sent by a meta" test.</summary>
    internal CommandReply Reply
    {
        get => _reply ??= new CommandReply((text, color) => _host.WriteToChat(text, color), line => _host.Log(line), () => IsExecutingAction);
        set => _reply = value;
    }

    /// <summary>Embedded Nav Route's "Patrol" (any case): the dungeon patrol instead of a route,
    /// unless the meta or NavProfiles has a route by that name.</summary>
    internal const string PatrolRoute = "Patrol";

    internal static bool IsPatrolRoute(string routeName) =>
        string.Equals(routeName?.Trim(), PatrolRoute, StringComparison.OrdinalIgnoreCase);

    public void SetObjectCache(WorldObjectCache cache)
    {
        _objectCache = cache;
        _expressions?.SetObjectCache(cache);
    }

    public void SetFellowshipTracker(FellowshipTracker tracker)
    {
        _fellowshipTracker = tracker;
        _expressions?.SetFellowshipTracker(tracker);
    }

    public void SetQuestTracker(QuestTracker tracker)
    {
        _questTracker = tracker;
        _expressions?.SetQuestTracker(tracker);
    }

    public void SetBuffManager(BuffManager buffManager) => _buffManager = buffManager;

    private CreatureProfileStore? _creatureStore;
    public void SetCreatureStore(CreatureProfileStore? store)
    {
        _creatureStore = store;
        _expressions?.SetCreatureStore(store);
    }

    // The plugin's kill clock (secondssincelastkill[], No Kills Within Minutes). Without one
    // (never in the plugin) the condition is false and the function says 0: fail closed.
    private KillClock? _killClock;
    public void SetKillClock(KillClock? clock)
    {
        _killClock = clock;
        _expressions?.SetKillClock(clock);
    }

    // Corpses this character opened (the plugin's loot bookkeeping), for ub-IT's
    // hascorpsebeenopenedbyme[] / getcorpsesunopenedbyme[].
    private Func<int, bool>? _corpseOpenedByMe;
    public void SetCorpseOpenedCheck(Func<int, bool>? check)
    {
        _corpseOpenedByMe = check;
        if (_expressions != null) _expressions.CorpseOpenedByMe = check;
    }

    // Combat's target count for ratargetcount[] (the last scan's candidates).
    private Func<int>? _combatTargetCount;
    public void SetCombatTargetCount(Func<int>? count)
    {
        _combatTargetCount = count;
        if (_expressions != null) _expressions.CombatTargetCount = count;
    }

    public void SetPlayerId(uint id)
    {
        _playerId = id;
        _expressions?.SetPlayerId(id);
    }

    private int FindNearestWaypoint(NavRouteParser route)
    {
        if (route.Points.Count == 0 || !NavCoordinateHelper.TryGetNavCoords(_host, out double ns, out double ew))
            return 0;

        int best = 0;
        double bestDistance = double.MaxValue;
        for (int i = 0; i < route.Points.Count; i++)
        {
            NavPoint point = route.Points[i];
            if (!NavRouteParser.IsPlainWaypoint(point.Type))
                continue;

            double distance = Math.Sqrt(Math.Pow(point.NS - ns, 2) + Math.Pow(point.EW - ew, 2));
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = i;
            }
        }

        return best;
    }

    /// <summary>
    /// Follow and Once routes must run from the first point so opening
    /// Recall/Portal/Chat actions fire. Circular and Linear routes jump
    /// in at the nearest traversable Point (at [0] with NavResumeNearest off).
    /// </summary>
    private int StartIndexForRoute(NavRouteParser route)
        => NavRouteParser.StartsAtFirstPoint(route.RouteType, _settings.NavResumeNearest)
            ? 0
            : FindNearestWaypoint(route);

    /// <summary>
    /// A replayed nav load (see <see cref="_replayRules"/>): true, and nothing reloaded, when
    /// <paramref name="path"/> is the route loaded now and it still has the points of its source
    /// <paramref name="source"/> (not edited, reversed or swapped for another since). The route
    /// keeps its waypoint and a Linear route its direction (the engine sees the same route
    /// object); nav is switched on as the load would, except for a finished Once route, which
    /// stays finished. False: the caller loads the route fresh, as before.
    /// </summary>
    private bool KeepRunningRoute(string path, NavRouteParser source, string what)
    {
        var cur = _settings.CurrentRoute;
        if (cur == null || cur.Points.Count == 0 || source.Points.Count == 0) return false;
        if (!SameNavPath(_settings.CurrentNavPath, path)) return false;
        if (!SameLines(cur.ToLines(), source.ToLines())) return false;

        bool finished = _settings.NavRouteFinished();
        if (!finished) _settings.EnableNavigation = true;
        RynthLog.Write(LogCat.Meta, finished
            ? $"[Meta] {what}: the macro started again on the same route, which is finished - it stays finished (/ra nav restart re-runs it)"
            : $"[Meta] {what}: the macro started again on the same route - it carries on at [{_settings.ActiveNavIndex}] instead of starting over");
        return true;
    }

    /// <summary>A replayed "/vt nav load" or "/ra nav load" of the .nav file that is still the loaded, unchanged route.</summary>
    private bool KeepRunningNavFile(string rawName)
    {
        string name = NewProfileFiles.Tidy(rawName, NewProfileFiles.NavExtensions, out _);
        if (NewProfileFiles.Validate(name) != null) return false;
        string path = Path.Combine(NavFolder, name + ".nav");
        if (!File.Exists(path)) return false;
        return KeepRunningRoute(path, NavRouteParser.Load(path), $"nav load '{name}'");
    }

    /// <summary>
    /// A replayed /ra nav command that would start the route over: "/ra nav load" of the running,
    /// unchanged route, or "/ra nav restart". True: skipped, the nav stays where it was.
    /// </summary>
    private bool SkipReplayedNavCommand(string cmd)
    {
        string[] parts = cmd.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3 || !parts[1].Equals("nav", StringComparison.OrdinalIgnoreCase)) return false;
        if (parts[2].Equals("restart", StringComparison.OrdinalIgnoreCase))
        {
            RynthLog.Write(LogCat.Meta, "[Meta] /ra nav restart: not run again for the macro start (it ran when the rule first fired)");
            return true;
        }
        return parts[2].Equals("load", StringComparison.OrdinalIgnoreCase) && parts.Length >= 4
            && KeepRunningNavFile(string.Join(" ", parts, 3, parts.Length - 3));
    }

    private static bool SameNavPath(string? a, string? b)
    {
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
        if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase)) return true;
        if (a.StartsWith('<') || b.StartsWith('<')) return false;   // "<embedded:...>" is not a file
        try { return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
    }

    private static bool SameLines(List<string> a, List<string> b)
    {
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++)
            if (!string.Equals(a[i], b[i], StringComparison.Ordinal)) return false;
        return true;
    }

    public ExpressionEngine Expressions => _expressions ??= CreateExpressionEngine();

    private ExpressionEngine CreateExpressionEngine()
    {
        var engine = new ExpressionEngine(_host);
        engine.SetPlayerId(_playerId);
        engine.SetObjectCache(_objectCache);
        engine.SetFellowshipTracker(_fellowshipTracker);
        engine.SetQuestTracker(_questTracker);
        engine.SetSettings(_settings);
        engine.SetCreatureStore(_creatureStore);
        engine.SetKillClock(_killClock);
        engine.CorpseOpenedByMe = _corpseOpenedByMe;
        engine.CombatTargetCount = _combatTargetCount;
        // vtsetsetting[] / vtgetsetting[] take VTank names and units, as '/vt opt set|get' does.
        engine.VtOptionSetter = TrySetVtOption;
        engine.VtOptionGetter = GetVtOption;
        return engine;
    }

    public void HandleChat(string text, int chatType = -1)
    {
        DateTime now = Clock();
        while (_chatLines.Count > 0 && ((now - _chatLines[0].At).TotalSeconds > 1.0 || _chatLines.Count >= 64))
            _chatLines.RemoveAt(0);
        _chatLines.Add(new ChatLine(++_chatSeq, now, text ?? "", chatType));
    }

    /// <summary>Writes pending setpvar/setgvar values now (they are otherwise
    /// throttled to one write every 2 s). Called when the session is torn down.</summary>
    public void FlushPendingVars() => _expressions?.FlushVars(force: true);

    /// <summary>The player died (called every tick while dead; one-shot for the meta).</summary>
    public void OnPlayerDeath() => _deathPending = true;

    public void OnVendorOpen(uint vendorId)
    {
        if (vendorId == 0) return;
        _openVendorId = vendorId;
    }

    public void OnVendorClose(uint vendorId)
    {
        if (vendorId == 0 || vendorId != _openVendorId) return;
        _openVendorId = 0;
        _vendorClosedThisTick = true;
    }

    public void Think()
    {
        // Drain delayexec on the plugin-tick thread (was a threadpool Timer).
        // Fires on tick cadence regardless of meta-enabled state, matching VTank.
        _expressions?.PumpDelayedExecs();
        _expressions?.FlushVars();   // throttled; no-op when nothing dirty

        try
        {
        if (!_settings.IsMacroRunning || !_settings.EnableMeta || _settings.MetaRules == null)
        {
            if (_lastMacroRunning && !_settings.IsMacroRunning)
                _expressions?.FlushVars(force: true);   // persist pending vars on stop
            _lastMacroRunning = _settings.IsMacroRunning;
            _vendorClosedThisTick = false;   // a close while stopped never fires later
            _deathPending = false;           // nor does a death
            return;
        }

        // A real state entry (anything but a bare macro stop/start) is logged. The persistent
        // seconds-in-state clock only restarts on a move to another state or a meta load.
        bool stateChanged = !string.Equals(_settings.CurrentState, _lastState, StringComparison.OrdinalIgnoreCase);
        bool realStateEntry = _settings.ForceStateReset || stateChanged;
        bool metaReloaded = _settings.MetaCallStackReset;

        // Macro just started — force reset so rules re-evaluate even if state
        // name matches where we stopped. Otherwise HasFired stays latched across
        // a stop/start cycle and IF:Always rules like EmbedNav never re-fire.
        // The rules that had already fired replay (see _replayRules): a replayed nav
        // load of the route still running keeps its place instead of starting it over.
        bool macroStarted = !_lastMacroRunning;
        if (macroStarted)
            _settings.ForceStateReset = true;
        _lastMacroRunning = true;

        // A freshly loaded meta starts with an empty call stack; a Return must
        // not pop a state name left over from the previous meta.
        if (_settings.MetaCallStackReset)
        {
            _settings.MetaCallStackReset = false;
            _stateStack.Clear();
            _statePPaused.Clear();
        }

        // ── State change / forced reset ──────────────────────────────────────
        // Only reset HasFired on ForceStateReset (set by meta actions: SetState,
        // CallState, ReturnFromCall, macro start, meta load, watchdog).
        // Operational state cycling (Combat/Looting/Default/Navigating/Buffing)
        // does NOT set ForceStateReset and must NOT re-fire latched rules —
        // otherwise EmbedNav reloads the nav route from index 0 on every cycle.
        if (_settings.ForceStateReset)
        {
            if (realStateEntry)
                _host.Log($"[Meta] state '{_lastState}' -> '{_settings.CurrentState}' after {(Clock() - _stateStartTime).TotalSeconds:0}s");
            _stateStartTime = Clock();
            _watchdogActive = false;
            if (metaReloaded)
                _statePStartTime = Clock();
            else if (stateChanged)
                _statePStartTime = _statePResume is double resumed ? Clock().AddSeconds(-resumed) : Clock();
            _statePResume = null;
            _settings.ForceStateReset = false;
            // A bare stop/start (no state entry, no meta load pending) replays the rules that
            // had fired; any other reset is a real state entry and nothing is a replay.
            bool replay = macroStarted && !realStateEntry && !metaReloaded;
            lock (_settings.MetaRulesLock)
            {
                _replayRules.Clear();
                foreach (var r in _settings.MetaRules)
                {
                    if (replay && r.HasFired) _replayRules.Add(r);
                    r.HasFired = false;
                }
            }
        }
        else if (stateChanged)
            _replayRules.Clear();
        _lastState = _settings.CurrentState;

        double secondsInState = (Clock() - _stateStartTime).TotalSeconds;

        // ── Portal state edge detection ───────────────────────────────────────
        bool currentPortal = _host.HasIsPortaling && _host.IsPortaling();
        _portalEnteredThisTick = currentPortal && !_lastPortalState;
        _portalExitedThisTick  = !currentPortal && _lastPortalState;
        _lastPortalState = currentPortal;

        // ── Vendor edge detection (one-shot) ──────────────────────────────────
        // OnVendorClose arrives between ticks. Hand the edge to this tick's
        // rule pass, then clear it; clearing it before the pass (as before)
        // meant VendorClosed could never be true.
        _vendorClosedNow = _vendorClosedThisTick;
        _vendorClosedThisTick = false;
        _deathNow = _deathPending;
        _deathPending = false;

        // ── Watchdog ─────────────────────────────────────────────────────────
        if (_watchdogActive &&
            _host.HasGetPlayerPose &&
            _host.TryGetPlayerPose(out _, out float wx, out float wy, out float wz, out _, out _, out _, out _))
        {
            var currentPos = new Vector3(wx, wy, wz);
            float distMoved = Vector3.Distance(currentPos, _lastWatchdogPos);

            if (distMoved < _watchdogMetersRequired)
            {
                if (Clock() > _watchdogExpiration)
                {
                    _watchdogActive = false;
                    // VTank calls the watchdog state: a Return in it re-enters this one.
                    PushCall(_settings.CurrentState);
                    _settings.CurrentState = _watchdogState;
                    _settings.ForceStateReset = true;
                    _host.WriteToChat($"[RynthAi] WATCHDOG TRIGGERED \u2192 {_watchdogState}", 1);
                    return;
                }
            }
            else
            {
                _lastWatchdogPos = currentPos;
                _watchdogExpiration = Clock().AddSeconds(_watchdogSecondsConfig);
            }
        }

        // ── Rule evaluation ───────────────────────────────────────────────────
        // Lock for the whole pass: this thread (plugin tick) races the Avalonia
        // dispatcher (MetaPanel poll / command handler) for _settings.MetaRules.
        // Monitor is reentrant, so an ExecuteAction that reassigns MetaRules
        // (e.g. /vt meta load) on this thread is safe.
        lock (_settings.MetaRulesLock)
        {
            if (!ReferenceEquals(_idxList, _settings.MetaRules)
                || _idxCount != _settings.MetaRules.Count
                || _idxVersion != _settings.MetaRulesStructuralVersion)
                RebuildStateIndex();

            _stateIndex.TryGetValue(_settings.CurrentState ?? "", out var bucket);
            foreach (var rule in bucket ?? _emptyRules)
            {
            if (rule.HasFired) continue;

            // Chat-capture groups are scoped to the rule that captured them:
            // clear before each rule so a ChatMessageCapture match can't bleed
            // its {0}/{1} groups into a later rule's action (or the next tick).
            _lastChatMatch = null;
            _evalRule = rule;
            _evalChatSeq = 0;

            if (EvaluateCondition(rule, secondsInState))
            {
                if (_evalChatSeq > 0) _chatConsumed[rule] = _evalChatSeq;
                rule.HasFired = true;
                rule.LastFiredAt = Clock();
                _lastFiredState = rule.State;
                _lastFiredCond  = DescribeCondition(rule);
                _lastFiredAct   = DescribeAction(rule);
                _lastFiredAt    = rule.LastFiredAt;

                // Always in the log (a rule fires at most once per state entry): a tester's meta
                // that stopped loading its nav left no trace of which state it was in or which
                // rules fired (2026-10-01). Chat only with MetaDebug.
                _host.Log($"[Meta] fired: state '{rule.State}' | {_lastFiredCond} -> {_lastFiredAct}");
                if (_settings.MetaDebug)
                    _host.WriteToChat($"[Meta] {rule.State} | {DescribeCondition(rule)} → {DescribeAction(rule)}", 1);

                _replaying = _replayRules.Contains(rule);
                try { ExecuteAction(rule); }
                finally { _replaying = false; }

                // If state changed during this action, stop evaluating further rules this tick
                if (_settings.CurrentState != _lastState || _settings.ForceStateReset)
                    break;
            }
        }
        }
        }
        catch (Exception ex)
        {
            // A condition/action threw. Localise it to this tick instead of
            // letting partial state (half-applied SetState, pushed call stack)
            // persist. ForceStateReset recomputes HasFired cleanly next tick.
            RynthLog.Write(LogCat.Meta, $"[Meta] Think crashed in '{_settings.CurrentState}': {ex.GetType().Name}: {ex.Message}");
            if (_settings.MetaDebug)
                _host.WriteToChat($"[Meta] Think exception: {ex.Message}", 1);
            _lastExprError   = $"Think: {ex.GetType().Name}: {Truncate(ex.Message, 100)}";
            _lastExprErrorAt = Clock();
            _settings.ForceStateReset = true;
        }
    }

    // ── Condition evaluation ─────────────────────────────────────────────────

    private bool EvaluateCondition(MetaRule rule, double secondsInState)
    {
        switch (rule.Condition)
        {
            case MetaConditionType.Always: return true;
            case MetaConditionType.Never:  return false;

            case MetaConditionType.All:
            {
                if (rule.Children == null || rule.Children.Count == 0) return true;
                foreach (var child in rule.Children)
                    if (!EvaluateCondition(child, secondsInState)) return false;
                return true;
            }

            case MetaConditionType.Any:
            {
                // VTank: an empty Any is false (and Not of it is true); metas use it as a "disabled" gate.
                if (rule.Children == null || rule.Children.Count == 0) return false;
                foreach (var child in rule.Children)
                    if (EvaluateCondition(child, secondsInState)) return true;
                return false;
            }

            case MetaConditionType.Not:
                if (rule.Children == null || rule.Children.Count == 0) return false;
                return !EvaluateCondition(rule.Children[0], secondsInState);

            // ── Vital checks (value) ──────────────────────────────────────────
            // Compared signed: a (uint) cast made a negative threshold ~4 billion,
            // so "health <= -1" was always true. MaxHealth > 0 = the vitals have been
            // read at least once (they are all 0 until the first poll after login).
            case MetaConditionType.MainHealthLE:
                return int.TryParse(rule.ConditionData, out int hv) && _vitals.MaxHealth > 0 && (long)_vitals.CurrentHealth <= hv;

            case MetaConditionType.MainManaLE:
                return int.TryParse(rule.ConditionData, out int mv) && _vitals.MaxHealth > 0 && (long)_vitals.CurrentMana <= mv;

            case MetaConditionType.MainStamLE:
                return int.TryParse(rule.ConditionData, out int sv) && _vitals.MaxHealth > 0 && (long)_vitals.CurrentStamina <= sv;

            // ── Vital checks (percentage) ─────────────────────────────────────
            // "Main Health/Mana % >=" (the UI label, the engine meta panel and
            // RynthScript's healthPct/manaPct >= N all mean >=, like VitaePHE).
            // These are RynthAi-only conditions (VTank has no vital conditions,
            // so no .met or metaf file carries them); the runtime used <=.
            case MetaConditionType.MainHealthPHE:
                return int.TryParse(rule.ConditionData, out int hpct) &&
                       _vitals.MaxHealth > 0 &&
                       (_vitals.CurrentHealth * 100.0 / _vitals.MaxHealth) >= hpct;

            case MetaConditionType.MainManaPHE:
                return int.TryParse(rule.ConditionData, out int mpct) &&
                       _vitals.MaxMana > 0 &&
                       (_vitals.CurrentMana * 100.0 / _vitals.MaxMana) >= mpct;

            case MetaConditionType.CharacterDeath:
                return _deathNow || (_vitals.MaxHealth > 0 && _vitals.CurrentHealth == 0);

            case MetaConditionType.VitaePHE:
            {
                if (!int.TryParse(rule.ConditionData, out int threshold)) return false;
                if (!_host.HasGetVitae || _playerId == 0) return false;
                float v = _host.GetVitae(_playerId);
                int penalty = 100 - (int)Math.Round(v * 100.0f);
                return penalty >= threshold;
            }

            // ── Time ──────────────────────────────────────────────────────────
            case MetaConditionType.SecondsInState_GE:
                return double.TryParse(rule.ConditionData, out double reqSecs) && secondsInState >= reqSecs;

            // VTank: "Persistent timer; does not reset if meta is stopped/started." As RynthScript
            // documents it, it also survives re-entry: a SetState to the same state, and a Call
            // and Return (paused while away). See _statePStartTime.
            case MetaConditionType.SecondsInStateP_GE:
                return double.TryParse(rule.ConditionData, out double reqPSecs)
                    && (Clock() - _statePStartTime).TotalSeconds >= reqPSecs;

            // ── Chat ──────────────────────────────────────────────────────────
            case MetaConditionType.ChatMessage:
            case MetaConditionType.ChatMessageCapture:
                return EvaluateChat(rule);

            // ── Pack slots ────────────────────────────────────────────────────
            case MetaConditionType.PackSlots_LE:
            {
                if (!int.TryParse(rule.ConditionData, out int targetSlots) || _objectCache == null || _playerId == 0) return false;
                // Empty slots in the MAIN pack (VTank "Pack Slots <="): only loose items
                // whose container is the player count. Side packs, their contents and
                // foci don't use the 102 item slots (same rule as GetMainPackEmptySlots).
                int used = 0;
                int pid = unchecked((int)_playerId);
                foreach (var item in _objectCache.GetDirectInventory())
                {
                    if (item.WieldedLocation > 0) continue;
                    if (item.Container != pid) continue;
                    if (item.ObjectClass is AcObjectClass.Container or AcObjectClass.Foci) continue;
                    if (item.Values(LongValueKey.EquippedSlots, 0) > 0) continue;
                    used++;
                }
                return (102 - used) <= targetSlots;
            }

            // ── Inventory item count ──────────────────────────────────────────
            case MetaConditionType.InventoryItemCount_LE:
            case MetaConditionType.InventoryItemCount_GE:
            {
                if (string.IsNullOrEmpty(rule.ConditionData) || _objectCache == null) return false;
                var parts = rule.ConditionData.Split(',');
                if (parts.Length < 2 || !int.TryParse(parts[1], out int targetCount)) return false;
                string itemName = parts[0].Trim();
                // The supply, not the number of stacks: a stack of 80 tapers is 80
                // (VTank's "Inventory Item Count"; getitemcountininventorybyname sums the same way).
                int count = 0;
                foreach (var item in _objectCache.GetDirectInventory())
                    if (item.Name.Equals(itemName, StringComparison.OrdinalIgnoreCase))
                        count += Math.Max(1, item.Values(LongValueKey.StackCount, 1));
                return rule.Condition == MetaConditionType.InventoryItemCount_LE
                    ? count <= targetCount
                    : count >= targetCount;
            }

            // ── Spell timer ───────────────────────────────────────────────────
            case MetaConditionType.TimeLeftOnSpell_GE:
            case MetaConditionType.TimeLeftOnSpell_LE:
            {
                if (string.IsNullOrEmpty(rule.ConditionData)) return false;
                var parts = rule.ConditionData.Split(',');
                if (parts.Length < 2 ||
                    !uint.TryParse(parts[0], out uint spellId) ||
                    !double.TryParse(parts[1], out double reqSeconds)) return false;
                if (!_host.HasReadPlayerEnchantments) return false;

                int count = _host.ReadPlayerEnchantments(_enchSpellIds, _enchExpiryTimes, 256);
                if (count <= 0)
                    return rule.Condition == MetaConditionType.TimeLeftOnSpell_LE;

                double serverTime = _host.HasGetServerTime ? _host.GetServerTime() : 0;
                for (int i = 0; i < count; i++)
                {
                    if (_enchSpellIds[i] == spellId)
                    {
                        double remaining = _enchExpiryTimes[i] - serverTime;
                        return rule.Condition == MetaConditionType.TimeLeftOnSpell_GE
                            ? remaining >= reqSeconds
                            : remaining <= reqSeconds;
                    }
                }
                // Spell not found = 0 time remaining
                return rule.Condition == MetaConditionType.TimeLeftOnSpell_LE;
            }

            // ── Monster count within distance ─────────────────────────────────
            case MetaConditionType.MonsterNameCountWithinDistance:
            {
                if (string.IsNullOrEmpty(rule.ConditionData) || _objectCache == null || _playerId == 0) return false;
                var parts = rule.ConditionData.Split(',');
                if (parts.Length < 3 ||
                    !double.TryParse(parts[1], out double maxDist) ||
                    !int.TryParse(parts[2], out int minCount)) return false;
                try
                {
                    var rx = RegexCache.Get(parts[0], RegexOptions.IgnoreCase);
                    if (rx == null) return false;
                    int matchCount = 0;
                    int pid = unchecked((int)_playerId);
                    foreach (var obj in _objectCache.GetLandscape())
                    {
                        if (obj.Id == pid) continue;
                        if (obj.ObjectClass != AcObjectClass.Monster) continue;
                        float hp = _objectCache.GetHealthRatio(obj.Id);
                        if (hp == 0f || hp < 0f) continue;
                        if (_host.HasObjectIsAttackable && !_host.ObjectIsAttackable(unchecked((uint)obj.Id))) continue;
                        if (_objectCache.Distance(obj.Id, pid) <= maxDist && rx.IsMatch(obj.Name))
                            matchCount++;
                    }
                    return matchCount >= minCount;
                }
                catch { return false; }
            }

            // ── No monsters within distance ───────────────────────────────────
            case MetaConditionType.NoMonstersWithinDistance:
            {
                if (_objectCache == null || _playerId == 0) return true;
                double.TryParse(rule.ConditionData, out double maxD);
                if (maxD <= 0) maxD = 20.0;
                int pid = unchecked((int)_playerId);
                foreach (var obj in _objectCache.GetLandscape())
                {
                    if (obj.Id == pid) continue; // never count self
                    if (obj.ObjectClass != AcObjectClass.Monster) continue;
                    float hp = _objectCache.GetHealthRatio(obj.Id);
                    if (hp == 0f) continue; // dead, pending reclassification
                    if (hp < 0f) continue;  // never received a health update — stale/untracked
                    if (_host.HasObjectIsAttackable && !_host.ObjectIsAttackable(unchecked((uint)obj.Id))) continue;
                    if (_objectCache.Distance(obj.Id, pid) <= maxD)
                        return false;
                }
                return true;
            }

            // ── Nav route empty ───────────────────────────────────────────────
            case MetaConditionType.NavrouteEmpty:
                if (_settings.CurrentRoute == null || _settings.CurrentRoute.Points.Count == 0) return true;
                return _settings.ActiveNavIndex >= _settings.CurrentRoute.Points.Count;

            // ── Landblock / landcell ──────────────────────────────────────────
            case MetaConditionType.Landblock_EQ:
            {
                if (string.IsNullOrEmpty(rule.ConditionData) || !_host.HasGetPlayerPose) return false;
                if (!_host.TryGetPlayerPose(out uint cellId, out _, out _, out _, out _, out _, out _, out _)) return false;
                uint landblock = cellId >> 16;
                if (uint.TryParse(rule.ConditionData, System.Globalization.NumberStyles.HexNumber, null, out uint hex))
                {
                    // metaf writes the full value ("BlockE 00070000", "BlockE 8B370000"),
                    // and VTank matches its leading 4 digits. RynthAi's own files use 4.
                    if (hex > 0xFFFF) hex >>= 16;
                    return landblock == hex;
                }
                if (uint.TryParse(rule.ConditionData, out uint dec))
                    return landblock == dec;
                return false;
            }

            case MetaConditionType.Landcell_EQ:
            {
                if (string.IsNullOrEmpty(rule.ConditionData) || !_host.HasGetPlayerPose) return false;
                if (!_host.TryGetPlayerPose(out uint cellId, out _, out _, out _, out _, out _, out _, out _)) return false;
                if (uint.TryParse(rule.ConditionData, System.Globalization.NumberStyles.HexNumber, null, out uint hex))
                    return cellId == hex;
                if (uint.TryParse(rule.ConditionData, out uint dec))
                    return cellId == dec;
                return false;
            }

            case MetaConditionType.PortalspaceEntered:
                return _portalEnteredThisTick;

            case MetaConditionType.PortalspaceExited:
                return _portalExitedThisTick;

            case MetaConditionType.AnyVendorOpen:
                return _openVendorId != 0;

            case MetaConditionType.VendorClosed:
                return _vendorClosedNow;

            case MetaConditionType.Expression:
            {
                if (string.IsNullOrEmpty(rule.ConditionData)) return false;
                string v = Expressions.Evaluate(rule.ConditionData);
                // Fail CLOSED on evaluation error. Evaluate() turns any
                // exception (and the recursion-depth guard) into an "ERR:…"
                // string; treating that as a fired condition silently inverts
                // safety gates ("flee if HP<20%" → "always flee").
                if (v.StartsWith("ERR:", StringComparison.Ordinal))
                {
                    _lastExprError   = $"{rule.State}: {Truncate(v, 120)}";
                    _lastExprErrorAt = Clock();
                    if (_settings.MetaDebug)
                        _host.WriteToChat($"[Meta] expr error in '{rule.State}': {Truncate(v, 80)}", 1);
                    return false;
                }
                return ExpressionEngine.ToBool(v);
            }

            // ── Burden percentage ─────────────────────────────────────────────
            case MetaConditionType.BurdenPercentage_GE:
            {
                if (!int.TryParse(rule.ConditionData, out int threshold)) return false;
                if (!_host.HasGetObjectIntProperty || _playerId == 0) return false;
                if (!_host.TryGetObjectIntProperty(_playerId, 5u, out int encumbVal)) return false;
                if (!_host.TryGetObjectIntProperty(_playerId, 96u, out int encumbCap) || encumbCap <= 0) return false;
                return (encumbVal * 100 / encumbCap) >= threshold;
            }

            // ── Distance to nearest nav route point ───────────────────────────
            case MetaConditionType.DistAnyRoutePT_GE:
            {
                if (!double.TryParse(rule.ConditionData, System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out double threshold)) return false;
                if (_settings.CurrentRoute == null || _settings.CurrentRoute.Points.Count == 0) return false;
                if (!NavCoordinateHelper.TryGetNavCoords(_host, out double ns, out double ew)) return false;

                double minDist = double.MaxValue;
                foreach (var pt in _settings.CurrentRoute.Points)
                {
                    if (!NavRouteParser.IsPlainWaypoint(pt.Type)) continue;
                    double d = Math.Sqrt((pt.NS - ns) * (pt.NS - ns) + (pt.EW - ew) * (pt.EW - ew));
                    if (d < minDist) minDist = d;
                }
                // NS/EW are map coordinates (1.0 = 240 yd); the threshold is in yards
                // (metaf: "shortest distance to current navroute >= Distance (in yards)").
                return minDist != double.MaxValue && minDist * 240.0 >= threshold;
            }

            // ── Priority monster count within distance ────────────────────────
            case MetaConditionType.MonsterPriorityCountWithinDistance:
            {
                if (_objectCache == null || _playerId == 0) return false;
                var parts = rule.ConditionData.Split(',');
                if (parts.Length < 2 ||
                    !int.TryParse(parts[0], out int minCount) ||
                    !double.TryParse(parts[1], out double maxDist)) return false;
                int pid = unchecked((int)_playerId);
                int matchCount = 0;
                foreach (var obj in _objectCache.GetLandscape())
                {
                    if (obj.ObjectClass != AcObjectClass.Monster) continue;
                    if (_objectCache.GetHealthRatio(obj.Id) == 0f) continue;
                    if (_host.HasObjectIsAttackable && !_host.ObjectIsAttackable(unchecked((uint)obj.Id))) continue;
                    if (_objectCache.Distance(obj.Id, pid) > maxDist) continue;
                    foreach (var mr in _settings.MonsterRules)
                    {
                        if (mr.Name.Equals("Default", StringComparison.OrdinalIgnoreCase)) continue;
                        if (RegexCache.IsMatch(obj.Name, mr.Name, RegexOptions.IgnoreCase))
                        { matchCount++; break; }
                    }
                }
                return matchCount >= minCount;
            }

            // ── Need to buff ──────────────────────────────────────────────────
            case MetaConditionType.NeedToBuff:
                return _buffManager != null && _buffManager.NeedsAnyBuff();

            // ── No kills within N minutes (RynthAi only) ──────────────────────
            // The secondssincelastkill[] clock: our last kill (KillerNotification), or before
            // the first one the last macro start / login. Bad or missing minutes never hold.
            case MetaConditionType.NoKillsWithinMinutes:
                return _killClock != null
                    && KillClock.TryParseMinutes(rule.ConditionData, out double noKillMins)
                    && _killClock.NoKillsFor(noKillMins);

            default: return false;
        }
    }

    /// <summary>
    /// True if a chat line from the last second (checked oldest first) matches
    /// the rule's pattern and the rule being evaluated hasn't already fired on
    /// that line or a later one.
    /// </summary>
    private bool EvaluateChat(MetaRule rule)
    {
        if (string.IsNullOrEmpty(rule.ConditionData) || _chatLines.Count == 0) return false;
        bool capture = rule.Condition == MetaConditionType.ChatMessageCapture;
        long consumed = _evalRule != null && _chatConsumed.TryGetValue(_evalRule, out long c) ? c : 0;
        DateTime now = Clock();
        for (int i = 0; i < _chatLines.Count; i++)
        {
            ChatLine line = _chatLines[i];
            if (line.Seq <= consumed) continue;
            if ((now - line.At).TotalSeconds > 1.0) continue;
            if (capture && !ChatColorListAllows(rule.ChatColors, line.Type)) continue;
            var match = RegexCache.Match(line.Text, rule.ConditionData);
            if (!match.Success) continue;
            if (line.Seq > _evalChatSeq) _evalChatSeq = line.Seq;
            if (capture)
            {
                _lastChatMatch = match;
                // VTank: each named group goes to capturegroup_<name>, the line's
                // colour (chat type) to capturecolor.
                foreach (Group g in match.Groups)
                    if (!int.TryParse(g.Name, out _))
                        Expressions.SetVariable("capturegroup_" + g.Name, g.Value);
                Expressions.SetVariable("capturecolor", line.Type.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
            return true;
        }
        return false;
    }

    /// <summary>VTank ChatCapture colour list ("2;4"): empty matches any line. A line
    /// whose chat type the engine didn't give (-1) isn't filtered.</summary>
    private static bool ChatColorListAllows(string? colors, int chatType)
    {
        if (string.IsNullOrWhiteSpace(colors) || chatType < 0) return true;
        foreach (string part in colors.Split(new[] { ';', ',', ' ' }, StringSplitOptions.RemoveEmptyEntries))
            if (int.TryParse(part, out int id) && id == chatType) return true;
        return false;
    }

    // ── Action execution ─────────────────────────────────────────────────────

    // Depth of ExecuteAction on the call stack: a rule's action (a chat command, a chat expression,
    // an expression action) is running. The plugin's MetaCommandActive reads it, so a command is
    // known to come from a running meta whichever way it reaches the plugin.
    private int _actionDepth;
    internal bool IsExecutingAction => _actionDepth > 0;

    private void ExecuteAction(MetaRule rule)
    {
        _actionDepth++;
        try { ExecuteActionCore(rule); }
        finally { _actionDepth--; }
    }

    private void ExecuteActionCore(MetaRule rule)
    {
        string ProcessData(string raw)
        {
            if (string.IsNullOrEmpty(raw) || _lastChatMatch == null || !_lastChatMatch.Success) return raw ?? "";
            string result = raw;
            for (int i = 0; i < _lastChatMatch.Groups.Count; i++)
                result = result.Replace($"{{{i}}}", _lastChatMatch.Groups[i].Value);
            return result;
        }

        switch (rule.Action)
        {
            case MetaActionType.All:
                if (rule.ActionChildren != null && rule.ActionChildren.Count > 0)
                    foreach (var child in rule.ActionChildren) ExecuteAction(child);
                else if (rule.Children != null)
                    foreach (var child in rule.Children) ExecuteAction(child);
                break;

            case MetaActionType.ChatCommand:
                string cmd = ProcessData(rule.ActionData);
                if (!string.IsNullOrEmpty(cmd))
                {
                    if (!TryHandleVtCommand(cmd) && !(_mtCommandHandler?.Invoke(cmd) == true))
                    {
                        if (cmd.StartsWith("/ra ", StringComparison.OrdinalIgnoreCase)
                            || cmd.Equals("/ra", StringComparison.OrdinalIgnoreCase))
                        {
                            // A replayed "/ra nav load" of the route still running, or "/ra nav restart",
                            // would start the route over: skipped.
                            if (!(_replaying && SkipReplayedNavCommand(cmd)))
                                _raCommandHandler?.Invoke(cmd);
                        }
                        else if (_host.HasInvokeChatParser)
                            _host.InvokeChatParser(cmd);
                        else
                            _host.WriteToChat($"[RynthAi] ChatCommand (no parser): {cmd}", 1);
                    }
                }
                break;

            case MetaActionType.SetMetaState:
                string nextState = ProcessData(rule.ActionData);
                if (!string.IsNullOrEmpty(nextState))
                {
                    _settings.CurrentState = nextState;
                    _settings.ForceStateReset = true;
                }
                break;

            case MetaActionType.CallMetaState:
                string callState = ProcessData(rule.ActionData);
                if (!string.IsNullOrEmpty(callState))
                {
                    // VTank pushes the rule's ReturnState; empty (older RynthAi rules) = this state.
                    string ret = ProcessData(rule.CallReturnState);
                    PushCall(string.IsNullOrEmpty(ret) ? _settings.CurrentState : ret);
                    _settings.CurrentState = callState;
                    _settings.ForceStateReset = true;
                }
                break;

            case MetaActionType.ReturnFromCall:
                if (_stateStack.Count > 0)
                {
                    _settings.CurrentState = _stateStack.Pop();
                    _statePResume = _statePPaused.Count > 0 ? _statePPaused.Pop() : null;
                    _settings.ForceStateReset = true;
                }
                break;

            case MetaActionType.EmbeddedNavRoute:
            {
                if (string.IsNullOrWhiteSpace(rule.ActionData))
                {
                    RynthLog.Write(LogCat.Meta, "[Meta] EmbedNav: empty ActionData, ignored");
                    break;
                }
                string routeName = EmbedNavName.Clean(rule.ActionData.Split(';')[0]);
                int priorPoints = _settings.CurrentRoute?.Points?.Count ?? 0;

                try
                {
                    // Embedded nav routes live in memory alongside the loaded meta.
                    // A key saved before names were cleaned ("None]", "[None]") still matches.
                    if (!_settings.EmbeddedNavs.ContainsKey(routeName))
                    {
                        foreach (var key in _settings.EmbeddedNavs.Keys)
                        {
                            if (string.Equals(EmbedNavName.Clean(key), routeName, StringComparison.OrdinalIgnoreCase))
                            {
                                routeName = key;
                                break;
                            }
                        }
                    }
                    // If the direct key misses, try normalizing legacy names
                    // like "nav0__MatronHive1_nav" → "MatronHive1".
                    if (!_settings.EmbeddedNavs.ContainsKey(routeName))
                    {
                        string norm = System.Text.RegularExpressions.Regex.Replace(
                            routeName, @"^nav\d+_+", "");
                        if (norm.EndsWith("_nav", StringComparison.OrdinalIgnoreCase))
                            norm = norm.Substring(0, norm.Length - 4);
                        norm = norm.Replace('_', ' ').Trim();
                        foreach (var key in _settings.EmbeddedNavs.Keys)
                        {
                            if (string.Equals(key, norm, StringComparison.OrdinalIgnoreCase) ||
                                string.Equals(key.Replace(".nav", "", StringComparison.OrdinalIgnoreCase), norm, StringComparison.OrdinalIgnoreCase))
                            {
                                routeName = key;
                                break;
                            }
                        }
                    }

                    // "Patrol" with no route of that name: the dungeon patrol (/ra dunnav-patrol, a
                    // circular hunt through every cell of this dungeon, no nav file). The Meta panel
                    // offers it first in Embedded Nav Route's list. A real route called Patrol wins.
                    if (IsPatrolRoute(routeName) && !_settings.EmbeddedNavs.ContainsKey(routeName)
                        && !File.Exists(Path.Combine(NavFolder, routeName + ".nav")))
                    {
                        _host.Log("[Meta] EmbedNav: Patrol -> /ra dunnav-patrol");
                        if (_raCommandHandler != null) _raCommandHandler("/ra dunnav-patrol");
                        else _host.WriteToChat("[RynthAi Meta] Patrol: no /ra command handler", 1);
                        break;
                    }

                    // VTank's "[None]" with no route behind it (no embedded route of that name, or an
                    // empty one) is an empty nav route, as in VTank: the current route is cleared, and no
                    // "None.nav" is looked for. A [None] route WITH points is a real route VTank loads
                    // (see EmbedNavName).
                    if (EmbedNavName.IsNone(routeName)
                        && (!_settings.EmbeddedNavs.TryGetValue(routeName, out var noneLines) || NavRouteParser.LoadFromLines(noneLines).Points.Count == 0))
                    {
                        _settings.CurrentNavPath = string.Empty;
                        _settings.CurrentRoute   = new NavRouteParser();
                        _settings.ActiveNavIndex = 0;
                        RynthLog.Write(LogCat.Meta, $"[Meta] EmbedNav: [None] - empty route (was {priorPoints} pts)");
                        break;
                    }

                    if (_settings.EmbeddedNavs.TryGetValue(routeName, out var embedded))
                    {
                        var newRoute = NavRouteParser.LoadFromLines(embedded);
                        // The macro was started again: the route still running keeps its place.
                        if (_replaying && KeepRunningRoute($"<embedded:{routeName}>", newRoute, $"EmbedNav '{routeName}'")) break;
                        _settings.CurrentNavPath   = $"<embedded:{routeName}>";
                        _settings.CurrentRoute     = newRoute;
                        _settings.ActiveNavIndex   = StartIndexForRoute(newRoute);
                        _settings.EnableNavigation = true;
                        RynthLog.Write(LogCat.Meta, $"[Meta] EmbedNav: loaded '{routeName}' ({newRoute.Points.Count} pts, was {priorPoints})");
                        _host.WriteToChat($"[RynthAi Meta] Route \u2192 {routeName} ({newRoute.Points.Count} pts)", 1);
                        break;
                    }

                    // Fallback: a standalone .nav file in NavProfiles.
                    string fullPath = Path.Combine(NavFolder, routeName + ".nav");
                    if (File.Exists(fullPath))
                    {
                        var newRoute = NavRouteParser.Load(fullPath);
                        if (_replaying && KeepRunningRoute(fullPath, newRoute, $"EmbedNav file '{routeName}'")) break;
                        _settings.CurrentNavPath   = fullPath;
                        _settings.CurrentRoute     = newRoute;
                        _settings.ActiveNavIndex   = StartIndexForRoute(newRoute);
                        _settings.EnableNavigation = true;
                        RynthLog.Write(LogCat.Meta, $"[Meta] EmbedNav: loaded file '{routeName}' ({newRoute.Points.Count} pts, was {priorPoints})");
                        _host.WriteToChat($"[RynthAi Meta] Route \u2192 {routeName} ({newRoute.Points.Count} pts)", 1);
                    }
                    else
                    {
                        string keys = string.Join(", ", _settings.EmbeddedNavs.Keys);
                        RynthLog.Write(LogCat.Meta, $"[Meta] EmbedNav: route '{routeName}' not found. Embedded keys: [{keys}]");
                        _host.WriteToChat($"[RynthAi Meta] Route missing: {routeName}", 1);
                    }
                }
                catch (Exception ex)
                {
                    RynthLog.Write(LogCat.Meta, $"[Meta] EmbedNav error: {ex.Message}");
                    _host.WriteToChat($"[RynthAi Meta] Load Error: {ex.Message}", 1);
                }
                break;
            }

            case MetaActionType.SetWatchdog:
            {
                if (_watchdogActive) break;
                var parts = (rule.ActionData ?? "").Split(';');
                if (parts.Length < 3) break;
                _watchdogState = parts[0].Trim();
                if (!float.TryParse(parts[1].Trim(), out _watchdogMetersRequired)) _watchdogMetersRequired = 5f;
                if (!double.TryParse(parts[2].Trim(), out _watchdogSecondsConfig)) _watchdogSecondsConfig = 5.0;
                _watchdogExpiration = Clock().AddSeconds(_watchdogSecondsConfig);
                if (_host.HasGetPlayerPose &&
                    _host.TryGetPlayerPose(out _, out float wx, out float wy, out float wz, out _, out _, out _, out _))
                    _lastWatchdogPos = new Vector3(wx, wy, wz);
                _watchdogActive = true;
                break;
            }

            case MetaActionType.ClearWatchdog:
                _watchdogActive = false;
                break;

            case MetaActionType.SetRAOption:
            {
                // VTank SetOpt {Option} {Expression}: evaluate the expression and set the
                // real option, the same way '/vt opt set' does. It used to store the raw
                // text in a private option table nothing reads, so SetOpt {EnableNav} {True}
                // (and every other SetOpt) did nothing.
                string optData = ProcessData(rule.ActionData);
                if (!string.IsNullOrEmpty(optData))
                {
                    var parts = optData.Split(';', 2);
                    if (parts.Length >= 2)
                    {
                        string raw = parts[1].Trim();
                        string value = Expressions.Evaluate(raw);
                        if (value.StartsWith("ERR:", StringComparison.Ordinal))
                        {
                            _lastExprError   = $"{rule.State}: SetOpt {parts[0].Trim()}: {Truncate(value, 100)}";
                            _lastExprErrorAt = Clock();
                            value = raw;
                        }
                        TrySetVtOption(parts[0].Trim(), value);
                    }
                }
                break;
            }

            case MetaActionType.GetRAOption:
            {
                string optData = ProcessData(rule.ActionData);
                if (!string.IsNullOrEmpty(optData))
                {
                    var parts = optData.Split(';');
                    if (parts.Length >= 2)
                    {
                        string varName = parts[0].Trim();
                        string optName = parts[1].Trim();
                        Expressions.SetVariable(varName, GetVtOption(optName) ?? "0");
                    }
                }
                break;
            }

            case MetaActionType.ChatExpression:
            {
                string exprSrc = ProcessData(rule.ActionData);
                if (!string.IsNullOrEmpty(exprSrc))
                {
                    string msg = Expressions.Evaluate(exprSrc);
                    if (!string.IsNullOrEmpty(msg))
                    {
                        if (!TryHandleVtCommand(msg) && !(_mtCommandHandler?.Invoke(msg) == true))
                        {
                            if (_host.HasInvokeChatParser)
                                _host.InvokeChatParser(msg);
                            else
                                _host.WriteToChat($"[RynthAi] ChatExpression (no parser): {msg}", 1);
                        }
                    }
                }
                break;
            }

            case MetaActionType.ExpressionAction:
                string exprText = ProcessData(rule.ActionData);
                if (!string.IsNullOrEmpty(exprText))
                {
                    if (!Expressions.TryExecuteAction(exprText))
                        _host.WriteToChat($"[RynthAi Expr] Unknown action: {exprText}", 1);
                }
                break;

            // ── View actions (VTank HUD/XML construct — NOT supported) ────────
            // Don't silently no-op (§2.8): a meta author using Views otherwise
            // sees nothing happen with no explanation. Warn once per session and
            // surface it on the debug strip.
            case MetaActionType.CreateView:
            case MetaActionType.DestroyView:
            case MetaActionType.DestroyAllViews:
                if (!_viewsWarned)
                {
                    _viewsWarned = true;
                    _lastExprError   = $"{rule.Action} ignored — VTank Views are not supported in RynthAi";
                    _lastExprErrorAt = Clock();
                    _host.WriteToChat("[RynthAi] Meta uses VTank Views (CreateView/DestroyView) — not supported; those actions are ignored.", 1);
                    RynthLog.Write(LogCat.Meta, $"[Meta] {rule.Action} ignored — VTank Views unsupported");
                }
                break;
        }
    }

    // ── VTank command translation ───────────────────────────────────────────

    /// <summary>
    /// Intercepts /vt commands from VTank metas and translates them to
    /// equivalent RynthAi settings changes. Returns true if handled.
    /// </summary>
    internal bool TryHandleVtCommand(string cmd)
    {
        if (!cmd.StartsWith("/vt ", StringComparison.OrdinalIgnoreCase))
            return false;

        string[] parts = cmd.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) return false;

        string sub = parts[1].ToLower();

        // /vt opt set <option> <value>
        if (sub == "opt" && parts.Length >= 5 &&
            parts[2].Equals("set", StringComparison.OrdinalIgnoreCase))
        {
            string optName = parts[3].ToLower();
            string optVal = parts[4];
            bool set = TrySetVtOption(optName, optVal);
            ReplyVtOption(parts[3], optVal);
            return set;
        }

        // /vt opt get <option> — show one option's value.
        if (sub == "opt" && parts.Length >= 4 &&
            parts[2].Equals("get", StringComparison.OrdinalIgnoreCase))
        {
            string optName = parts[3];
            Reply.Info($"[RynthAi] {optName} = {GetVtOption(optName) ?? "(not set)"}");
            return true;
        }

        // /vt opt list — every VTank option RynthAi maps, with its value.
        if (sub == "opt" && parts.Length >= 3 &&
            parts[2].Equals("list", StringComparison.OrdinalIgnoreCase))
        {
            var line = new System.Text.StringBuilder();
            foreach (string name in System.Linq.Enumerable.OrderBy(VtOptionMap.Keys, k => k, StringComparer.OrdinalIgnoreCase))
            {
                string item = $"{name}={GetVtOption(name) ?? "?"}";
                if (line.Length + item.Length > 180) { Reply.Info("[RynthAi] " + line); line.Clear(); }
                if (line.Length > 0) line.Append(", ");
                line.Append(item);
            }
            if (line.Length > 0) Reply.Info("[RynthAi] " + line);
            return true;
        }

        // /vt start | /vt stop — the macro, as VTank's own start/stop.
        if (sub is "start" or "stop")
        {
            _raCommandHandler?.Invoke("/ra " + sub);
            return true;
        }

        // /vt e — VTank's macro toggle (ub-IT sends it to switch VTank on or off).
        if (sub == "e")
        {
            _raCommandHandler?.Invoke(_settings.IsMacroRunning ? "/ra stop" : "/ra start");
            return true;
        }

        // /vt jump [heading] [shift true|false] [holdms] — VTank's jump (universalremote.met, ub-IT):
        // RynthAi's jumper through /ra jump[s] [heading] [holdtime].
        if (sub == "jump")
        {
            string heading = parts.Length >= 3 ? parts[2] : "";
            bool shift = parts.Length >= 4 && (parts[3].Equals("true", StringComparison.OrdinalIgnoreCase) || parts[3] == "1");
            string hold = parts.Length >= 5 ? parts[4] : "";
            _raCommandHandler?.Invoke(("/ra jump" + (shift ? "s" : "") + " " + heading + " " + hold).Trim());
            return true;
        }

        // /vt tapjump — a quick jump with no charge.
        if (sub == "tapjump")
        {
            if (_host.HasTapJump && _host.TapJump()) Reply.Ok("[RynthAi] Tap jump.");
            else Reply.Fail("[RynthAi] /vt tapjump: the engine's jump isn't available.");
            return true;
        }

        // /vt equipitemsfor <monster> — wield what that monster's rule names (weapon, off hand).
        if (sub == "equipitemsfor" && parts.Length >= 3)
        {
            _raCommandHandler?.Invoke("/ra equipfor " + string.Join(" ", parts, 2, parts.Length - 2));
            return true;
        }

        // /vt meta load <name> — the same load as /ra meta load (LegacyDashboardRenderer.LoadMetaByName):
        // a meta in MetaFiles, or, when there is none by that name, a new blank one (2026-10-13).
        if (sub == "meta" && parts.Length >= 4 &&
            parts[2].Equals("load", StringComparison.OrdinalIgnoreCase))
        {
            string name = string.Join(" ", parts, 3, parts.Length - 3);
            _raCommandHandler?.Invoke($"/ra meta load {name}");
            return true;
        }

        // /vt nav load <name> — load a nav route
        // It used to store only the bare name as the nav path, so nothing loaded.
        // /vt nav save <name> saves the current route under that name.
        if (sub == "nav" && parts.Length >= 4 &&
            (parts[2].Equals("load", StringComparison.OrdinalIgnoreCase)
             || parts[2].Equals("save", StringComparison.OrdinalIgnoreCase)))
        {
            string name = string.Join(" ", parts, 3, parts.Length - 3);
            // The macro was started again: the route still running keeps its place.
            if (_replaying && parts[2].Equals("load", StringComparison.OrdinalIgnoreCase) && KeepRunningNavFile(name))
            {
                Reply.Ok($"[RynthAi] Nav '{name}' is the route already running; it keeps its place.");
                return true;
            }
            _raCommandHandler?.Invoke($"/ra nav {parts[2].ToLowerInvariant()} {name}");
            return true;
        }

        // /vt loot load <name> — load a loot profile ("looting load" too: FoundryIzexi.met, Meta_Society.met)
        if ((sub == "loot" || sub == "lootprofile" || sub == "looting") && parts.Length >= 4 &&
            parts[2].Equals("load", StringComparison.OrdinalIgnoreCase))
        {
            string name = string.Join(" ", parts, 3, parts.Length - 3).Trim().Trim('"');
            // A bare name is a file in LootProfiles (.utl, else .json). Storing the
            // bare name made the loader report "not found" and looting stopped.
            string path = name;
            if (!File.Exists(path))
            {
                string folder = System.IO.Path.Combine(RynthCore.Install.RynthInstallPaths.RynthAiDir, "LootProfiles");
                string stem = name.EndsWith(".utl", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                    ? name : name + ".utl";
                string candidate = Path.Combine(folder, stem);
                if (!File.Exists(candidate) && !stem.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                    candidate = Path.Combine(folder, name + ".json");
                path = candidate;
            }
            if (!File.Exists(path))
            {
                Reply.Fail($"[RynthAi] Loot profile not found: {name}");
                return true;
            }
            _settings.CurrentLootPath = path;
            Reply.Ok($"[RynthAi] Loot profile set: {Path.GetFileName(path)}");
            return true;
        }

        // /vt setmetastate <state> — switch the active meta state
        if (sub == "setmetastate" && parts.Length >= 3)
        {
            string state = string.Join(" ", parts, 2, parts.Length - 2);
            _settings.CurrentState = state;
            _settings.ForceStateReset = true;
            Reply.Ok($"[RynthAi] Meta state set: {state}");
            return true;
        }

        // /vt settings loadchar|savechar <name> — RynthAi's settings profiles (/ra settings ...);
        // universalremote.met switches profiles this way. Any other /vt settings verb is ignored.
        if (sub == "settings")
        {
            if (parts.Length >= 4 && (parts[2].Equals("loadchar", StringComparison.OrdinalIgnoreCase)
                                      || parts[2].Equals("savechar", StringComparison.OrdinalIgnoreCase)))
                _raCommandHandler?.Invoke($"/ra settings {parts[2].ToLowerInvariant()} {string.Join(" ", parts, 3, parts.Length - 3)}");
            else
                Reply.Ignored("[RynthAi] Usage: /vt settings loadchar|savechar <name> (other /vt settings verbs are VTank's own; ignored).");
            return true;
        }

        // /vt <option> <value> — an option written as a verb (DerethExploration: "/vt navclosestoprange
        // 0.005"): the same as /vt opt set, for the VTank options RynthAi maps.
        if (parts.Length == 3 && (VtOptionMap.ContainsKey(sub) || IsSpecialVtOption(sub)))
        {
            bool set = TrySetVtOption(sub, parts[2]);
            ReplyVtOption(parts[1], parts[2]);
            return set;
        }

        // /vt <verb> aliases → existing /ra handlers (Phase 1.3 VTank-meta migration).
        // Re-dispatch through the /ra path (_raCommandHandler → HandleRaCommand).
        if (sub is "forcebuff" or "cancelforcebuff" or "addnavpt" or "mexec" or "clearbusy" or "testitem" or "propertydump")
        {
            _raCommandHandler?.Invoke("/ra " + string.Join(" ", parts, 1, parts.Length - 1));
            return true;
        }

        // /vt setattackbar <0..1 fraction> → RynthAi attack power % (reuse /ra power,
        // which sets Melee+Missile AttackPower). VTank metas pass a 0-1 bar fraction.
        if (sub == "setattackbar" && parts.Length >= 3 &&
            double.TryParse(parts[2], System.Globalization.NumberStyles.Any,
                            System.Globalization.CultureInfo.InvariantCulture, out double bar))
        {
            int pct = Math.Clamp((int)Math.Round(bar * 100), 0, 100);
            _lastAttackBarPct = pct;
            _raCommandHandler?.Invoke("/ra power " + pct.ToString(System.Globalization.CultureInfo.InvariantCulture));
            return true;
        }

        // /vt deletemonster (S_Colo, leaving the arena): no RynthAi equivalent is known. Accepted so
        // it doesn't go on to AC's chat parser as an unknown command; logged once.
        if (sub == "deletemonster")
        {
            if (!_deleteMonsterLogged)
            {
                _deleteMonsterLogged = true;
                RynthLog.Write(LogCat.Meta, "[Meta] /vt deletemonster accepted and ignored (no RynthAi equivalent).");
            }
            Reply.Ignored("[RynthAi] /vt deletemonster has no RynthAi equivalent; accepted and ignored.");
            return true;
        }

        // /vt reverseroute — reverse the active nav route in place (shared settings;
        // the engine re-reads CurrentRoute each tick, so the flip takes effect next tick).
        if (sub == "reverseroute")
        {
            var route = _settings.CurrentRoute;
            if (route?.Points != null && route.Points.Count > 0)
            {
                int n = route.Points.Count;
                route.Points.Reverse();
                _settings.ActiveNavIndex = Math.Clamp(n - 1 - _settings.ActiveNavIndex, 0, n - 1);
                Reply.Ok($"[RynthAi] Route reversed ({n} pts).");
            }
            else Reply.Fail("[RynthAi] /vt reverseroute: no route is loaded.");
            return true;
        }

        // /vt echo <text> — local chat echo (no server send).
        if (sub == "echo" && parts.Length >= 3)
        {
            Reply.Info(string.Join(" ", parts, 2, parts.Length - 2));
            return true;
        }

        // /vt cancelbuff — cancel the buff sequence (same capability as /ra cancelforcebuff).
        if (sub == "cancelbuff")
        {
            _raCommandHandler?.Invoke("/ra cancelforcebuff");
            return true;
        }

        return false;
    }

    /// <summary>
    /// After a /vt opt set (or "/vt &lt;option&gt; &lt;value&gt;"): the option's value now, and whether RynthAi
    /// has a setting by that name (otherwise it is only kept for metas to read back).
    /// </summary>
    private void ReplyVtOption(string vtName, string asked)
    {
        bool known = IsSpecialVtOption(vtName) || VtOptionMap.ContainsKey(vtName)
                     || Expressions.BuildSettingsMapPublic().ContainsKey(vtName);
        string now = GetVtOption(vtName) ?? "(not set)";
        // A switch asked for as true/false/on/off reads back as 1/0: shown the way it was asked.
        if (asked.ToLowerInvariant() is "true" or "false" or "on" or "off" && now is "1" or "0" or "true" or "false")
            now = now is "1" or "true" ? "True" : "False";
        Reply.Ok($"[RynthAi] {vtName} = {now}"
                 + (known ? "" : " (no RynthAi setting by that name: kept only as an option metas can read)"));
    }

    /// <summary>The /vt verbs RynthAi takes, for the usage line.</summary>
    internal const string VtVerbList = "opt set|get|list, <option> <value>, start, stop, e, jump, tapjump, equipitemsfor, "
        + "meta load, nav load|save, loot load, setmetastate, settings loadchar|savechar, setattackbar, forcebuff, "
        + "cancelforcebuff, cancelbuff, addnavpt, mexec, clearbusy, testitem, propertydump, reverseroute, echo";

    /// <summary>The usage line for a /vt line RynthAi didn't take (an unknown verb or the wrong arguments).</summary>
    internal static string VtUsageFor(string line)
    {
        string[] w = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        string verb = w.Length > 1 ? w[1].ToLowerInvariant() : "";
        string? usage = verb switch
        {
            "opt" => "/vt opt set <option> <value> | /vt opt get <option> | /vt opt list",
            "meta" => "/vt meta load <name>",
            "nav" => "/vt nav load <name> | /vt nav save <name>",
            "loot" or "lootprofile" or "looting" => "/vt loot load <profile>",
            "setmetastate" => "/vt setmetastate <state>",
            "equipitemsfor" => "/vt equipitemsfor <monster name>",
            "setattackbar" => "/vt setattackbar <0 to 1>",
            "echo" => "/vt echo <text>",
            _ => null,
        };
        return usage != null
            ? "[RynthAi] Usage: " + usage
            : $"[RynthAi] Unknown /vt command{(verb.Length > 0 ? $" '{verb}'" : "")}. RynthAi takes: {VtVerbList}.";
    }

    /// <summary>The RynthAi setting a VTank option name maps to (EnableNav → EnableNavigation).</summary>
    internal static bool TryMapVtOption(string vtName, out string raName)
    {
        if (VtOptionMap.TryGetValue(vtName, out string? n)) { raName = n; return true; }
        raName = "";
        return false;
    }

    /// <summary>VTank bool text (true/false/on/off, any case) as 1/0; anything else unchanged.</summary>
    internal static string NormalizeVtValue(string vtValue) => vtValue.ToLowerInvariant() switch
    {
        "true"  => "1",
        "false" => "0",
        "on"    => "1",
        "off"   => "0",
        _       => vtValue
    };

    // VTank option name → RynthAi settings mapping
    private static readonly Dictionary<string, string> VtOptionMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["enablecombat"]              = "EnableCombat",
        ["enablelooting"]             = "EnableLooting",
        ["enablenav"]                 = "EnableNavigation",
        ["enablebuffing"]             = "EnableBuffing",
        ["enablemeta"]                = "EnableMeta",
        ["opendoors"]                 = "OpenDoors",
        ["idlepeacemode"]             = "PeaceModeWhenIdle",
        ["idlebufftopoff"]            = "RebuffWhenIdle",
        ["summonpets"]                = "SummonPets",
        ["combinesalvage"]            = "EnableCombineSalvage",
        ["attackdistance"]            = "MonsterRange",
        ["approachdistance"]          = "ApproachRange",
        ["navpriorityboost"]          = "BoostNavPriority",
        ["lootpriorityboost"]         = "BoostLootPriority",
        ["dooropenrange"]             = "OpenDoorRange",
        ["autofellowmanagement"]      = "AutoFellowMgmt",
        ["switchwandstodebuff"]       = "UseDispelItems",
        ["lootonlyrarecorpses"]       = "MineOnly",
        // Phase 1.2 VTank-meta migration aliases (targets verified in BuildSettingsMap)
        ["autocram"]                  = "EnableAutocram",
        ["autostack"]                 = "EnableAutostack",
        ["readunknownscrolls"]        = "ReadUnknownScrolls",      // VTank: loots and reads unknown scrolls
        ["usedispelitems"]            = "UseDispelItems",
        ["castdispelself"]            = "CastDispelSelf",
        ["opendoorrange"]             = "OpenDoorRange",
        // Phase 2 migration: vital recharge is 2-tier — norm = with-target/in-combat,
        // notarg = idle/no-target (RynthAi has both: HealAt/… vs TopOff…). Percent 0-100.
        ["recharge-norm-hitp"]        = "HealAt",
        ["usekitsinmagicmode"]        = "UseKitsInMagicMode",
        ["gotopeacemodetousekits"]    = "PeaceModeForKits",
        ["stopmacroondeath"]          = "StopMacroOnDeath",
        ["recharge-norm-mana"]        = "GetManaAt",
        ["recharge-norm-stam"]        = "RestamAt",
        ["recharge-notarg-hitp"]      = "TopOffHP",
        ["recharge-notarg-mana"]      = "TopOffMana",
        ["recharge-notarg-stam"]      = "TopOffStam",
        ["petmonsterdensity"]         = "PetMinMonsters",
        ["corpseapproachrange-max"]   = "CorpseApproachRangeMax",  // landblock fraction; consumer ×240
        ["corpseapproachrange-min"]   = "CorpseApproachRangeMin",
        ["manastonelootcount"]        = "ManaStoneKeepCount",
        ["rebuftimeremainingseconds"] = "RebuffSecondsRemaining",
        // VTank's "Follow/Nav Min Distance" box: landblock units (x240 = yards). The settings map
        // converts it into FollowNavMin, the nav point reach (see ExpressionEngine).
        ["navclosestoprange"]         = "NavCloseStopRange",
        ["follownavmin"]              = "FollowNavMin",             // RynthAi's own name, yards
        // 2026-10-17: options the metas in MetaFiles / MetaProfiles set that had no mapping.
        ["navclosetoprange"]          = "NavCloseStopRange",        // the same option, misspelt in some metas
        ["attackrange"]               = "MonsterRange",
        ["ringdistance"]              = "RingRange",
        ["minimumringtargets"]        = "MinRingTargets",
        ["corpseapproachrange"]       = "CorpseApproachRangeMax",
        ["corpseapproachrange-max"]   = "CorpseApproachRangeMax",
        ["corpseapproachrange-min"]   = "CorpseApproachRangeMin",
        // 2026-10-18: the sample metas (S_*, Colo) set these and nothing read them.
        ["rebufftimeremainingseconds"] = "RebuffSecondsRemaining",  // VTank's spelling (the one above is a typo)
        ["petcustomrange"]            = "CustomPetRange",           // landblock units -> whole yards
        ["blacklistmonstertimeoutseconds"] = "BlacklistTimeoutSec",
    };

    /// <summary>VTank options given in landblock units (x240 = yards) whose RynthAi setting is yards.
    /// NavCloseStopRange converts itself (the settings map takes landblock units).</summary>
    private static readonly HashSet<string> VtDistanceOptions = new(StringComparer.OrdinalIgnoreCase)
    {
        "attackdistance", "attackrange", "approachdistance", "dooropenrange", "opendoorrange", "ringdistance",
        "corpseapproachrange", "corpseapproachrange-max", "corpseapproachrange-min", "petcustomrange",
    };

    /// <summary>RynthAi distance settings kept in whole yards (their setter refuses a decimal).</summary>
    private static readonly HashSet<string> IntYardSettings = new(StringComparer.Ordinal)
    {
        "MonsterRange", "ApproachRange", "RingRange", "CustomPetRange",
    };

    /// <summary>A RynthAi yards value as VTank's landblock units (yards / 240), for reading a distance back.</summary>
    internal static string YardsToVtDistance(string yards)
    {
        if (!double.TryParse(yards, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double v))
            return yards;
        return (v / 240.0).ToString("0.######", System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// A VTank distance as RynthAi yards: a value up to 1 is landblock units (x240; VTank's own
    /// unit), a larger one is taken as yards already (typed by hand). Invariant culture.
    /// </summary>
    internal static string VtDistanceToYards(string value, bool wholeYards)
    {
        if (!double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double v))
            return value;   // not a number (an unexpanded expression): left for the setter to refuse
        double yards = v > 0 && v <= 1.0 ? v * 240.0 : v;
        return wholeYards
            ? Math.Max(0, (int)Math.Round(yards)).ToString(System.Globalization.CultureInfo.InvariantCulture)
            : yards.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>VTank options handled by <see cref="TrySetSpecialVtOption"/> (no 1:1 RynthAi setting).</summary>
    private static bool IsSpecialVtOption(string vtName) => vtName.ToLowerInvariant() is
        "defaultmeleeattackheight" or "lootfellowcorpses" or "lootallcorpses" or "usearcs" or "minimumhealkitsuccesschance"
        or "autoattackpower";

    // The last /vt setattackbar power (percent), for AutoAttackPower false after it was on (-1 = none yet).
    private int _lastAttackBarPct = -1;
    private bool _deleteMonsterLogged;

    /// <summary>
    /// The VTank options whose value means something else in RynthAi:
    /// DefaultMeleeAttackHeight 1-3 (low/medium/high) is RynthAi's 0-2; LootFellowCorpses /
    /// LootAllCorpses pick the loot-ownership mode (my kills / fellowship / all); UseArcs is a VTank
    /// mode number (2, 3 in the metas) for RynthAi's on/off; MinimumHealKitSuccessChance is a
    /// percent (a fraction up to 1 is scaled). False when <paramref name="vtName"/> isn't one of them.
    /// </summary>
    private bool TrySetSpecialVtOption(string vtName, string value)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        bool num = double.TryParse(value, System.Globalization.NumberStyles.Float, inv, out double n);
        switch (vtName.ToLowerInvariant())
        {
            case "defaultmeleeattackheight":
                if (!num) return true;
                _settings.MeleeAttackHeight = Math.Clamp((int)Math.Round(n) - 1, 0, 2);
                return true;
            case "lootfellowcorpses":
                if (!num) return true;
                if (n != 0 && _settings.LootOwnership < 1) _settings.LootOwnership = 1;
                else if (n == 0 && _settings.LootOwnership == 1) _settings.LootOwnership = 0;
                return true;
            case "lootallcorpses":
                if (!num) return true;
                if (n != 0) _settings.LootOwnership = 2;
                else if (_settings.LootOwnership == 2) _settings.LootOwnership = 0;
                return true;
            case "usearcs":
                if (!num) return true;
                _settings.UseArcs = n != 0;
                return true;
            case "minimumhealkitsuccesschance":
                if (!num) return true;
                _settings.KitMinSuccessPct = Math.Clamp((int)Math.Round(n > 0 && n <= 1 ? n * 100 : n), 0, 100);
                return true;
            case "autoattackpower":
                // On: RynthAi picks the power itself (-1, its "auto"). Off: the attack bar's value,
                // the last /vt setattackbar (a power already set by hand stays as it is).
                if (!num) return true;
                if (n != 0)
                {
                    _settings.MeleeAttackPower = _settings.MissileAttackPower = -1;
                }
                else if (_settings.MeleeAttackPower < 0 || _settings.MissileAttackPower < 0)
                {
                    if (_lastAttackBarPct >= 0)
                        _settings.MeleeAttackPower = _settings.MissileAttackPower = _lastAttackBarPct;
                    else
                        RynthLog.Write(LogCat.Meta, "[Meta] AutoAttackPower off with no /vt setattackbar value yet: attack power stays automatic.");
                }
                return true;
            default:
                return false;
        }
    }

    /// <summary>The special options as VTank reads them back (height 1-3, ownership as two flags).</summary>
    private string? GetSpecialVtOption(string vtName) => vtName.ToLowerInvariant() switch
    {
        "defaultmeleeattackheight" => (_settings.MeleeAttackHeight + 1).ToString(System.Globalization.CultureInfo.InvariantCulture),
        "lootfellowcorpses" => _settings.LootOwnership >= 1 ? "true" : "false",
        "lootallcorpses" => _settings.LootOwnership == 2 ? "true" : "false",
        "usearcs" => _settings.UseArcs ? "true" : "false",
        "minimumhealkitsuccesschance" => _settings.KitMinSuccessPct.ToString(System.Globalization.CultureInfo.InvariantCulture),
        "autoattackpower" => _settings.MeleeAttackPower < 0 ? "true" : "false",
        _ => null,
    };

    /// <summary>A VTank option's current value (mapped setting, else a stored generic option).</summary>
    private string? GetVtOption(string vtName)
    {
        if (GetSpecialVtOption(vtName) is string special) return special;
        var map = Expressions.BuildSettingsMapPublic();
        if (VtOptionMap.TryGetValue(vtName, out string? raName))
        {
            // Distances read back in VTank's landblock units, as they were set (6 yd reads 0.025).
            if (map.TryGetValue(raName, out var entry))
                return VtDistanceOptions.Contains(vtName) ? YardsToVtDistance(entry.Get()) : entry.Get();
        }
        // A RynthAi setting named directly (EnableCombat, OpenDoors, NavCloseStopRange...).
        if (map.TryGetValue(vtName, out var direct)) return direct.Get();
        string v = Expressions.GetOption(vtName);
        return string.IsNullOrEmpty(v) ? null : v;
    }

    private bool TrySetVtOption(string vtName, string vtValue)
    {
        // Translate VTank bool strings to numeric
        string value = NormalizeVtValue(vtValue);

        // Options whose value means something else in RynthAi (a scale, an offset, a mode).
        if (TrySetSpecialVtOption(vtName, value)) return true;

        // VTank distances are in landblock units (1 = 240 yd: AttackDistance .01666 is 4 yd); RynthAi's
        // are yards. Converted here, rounded where RynthAi keeps whole yards (an int setting refused
        // "0.01666" outright, so every meta's attack / approach distance was dropped).
        if (VtDistanceOptions.Contains(vtName) && VtOptionMap.TryGetValue(vtName, out string? distName))
            value = VtDistanceToYards(value, IntYardSettings.Contains(distName));

        var map = Expressions.BuildSettingsMapPublic();
        if (VtOptionMap.TryGetValue(vtName, out string? raName))
        {
            if (map.TryGetValue(raName, out var entry))
            {
                entry.Set(value);
                return true;
            }
        }
        // A RynthAi setting named directly (EnableCombat, OpenDoors, NavCloseStopRange...).
        if (map.TryGetValue(vtName, out var direct))
        {
            direct.Set(value);
            return true;
        }

        // Not mapped — store as a generic option so expressions can still read it
        Expressions.SetOption(vtName, value);
        return true;
    }

    // ── Debug helpers ─────────────────────────────────────────────────────────

    private static string DescribeCondition(MetaRule rule)
    {
        string cond = rule.Condition.ToString();
        if (!string.IsNullOrEmpty(rule.ConditionData))
            cond += $"({Truncate(rule.ConditionData, 40)})";
        return cond;
    }

    private static string DescribeAction(MetaRule rule)
    {
        string act = rule.Action.ToString();
        if (!string.IsNullOrEmpty(rule.ActionData))
            act += $"({Truncate(rule.ActionData, 60)})";
        else if (rule.Action == MetaActionType.All)
        {
            int count = (rule.ActionChildren?.Count ?? 0) + (rule.Children?.Count ?? 0);
            act += $"({count} sub-actions)";
        }
        return act;
    }

    private static string Truncate(string s, int max)
        => s.Length <= max ? s : s[..max] + "…";

    // ── Per-state rule index (§4) ─────────────────────────────────────────────

    /// <summary>Rebuilds the state→rules buckets. Caller MUST hold MetaRulesLock.</summary>
    private void RebuildStateIndex()
    {
        _stateIndex.Clear();
        _chatConsumed.Clear();   // drop rules that are gone; at worst a line is seen again
        var list = _settings.MetaRules;
        foreach (var r in list)
        {
            if (!r.Enabled) continue;   // disabled rules never enter a bucket → never evaluate
            string st = r.State ?? "";
            if (!_stateIndex.TryGetValue(st, out var b))
                _stateIndex[st] = b = new List<MetaRule>();
            b.Add(r);
        }
        _idxList = list;
        _idxCount = list.Count;
        _idxVersion = _settings.MetaRulesStructuralVersion;
    }

    // ── Observability snapshot (§3.4) ─────────────────────────────────────────

    /// <summary>
    /// Point-in-time view of the meta state machine for the diag/UI surface.
    /// MetaRules counts are read under the shared lock; the scalar timing
    /// fields are best-effort cross-thread reads (worst case: a momentarily
    /// stale SecondsInState in the UI — never affects bot behaviour).
    /// </summary>
    public MetaSnapshot GetStateSnapshot()
    {
        var s = new MetaSnapshot
        {
            MacroRunning   = _settings.IsMacroRunning,
            MetaEnabled    = _settings.EnableMeta,
            CurrentState   = _settings.CurrentState ?? "",
            SecondsInState = (Clock() - _stateStartTime).TotalSeconds,
            StackDepth     = _stateStack.Count,
            WatchdogActive = _watchdogActive,
            WatchdogState  = _watchdogState ?? "",
            WatchdogSecondsRemaining = _watchdogActive
                ? Math.Max(0, (_watchdogExpiration - Clock()).TotalSeconds) : 0,
            LastFiredState     = _lastFiredState,
            LastFiredCondition = _lastFiredCond,
            LastFiredAction    = _lastFiredAct,
            LastFiredSecondsAgo = _lastFiredAt == DateTime.MinValue
                ? -1 : (Clock() - _lastFiredAt).TotalSeconds,
            LastExprError = _lastExprError,
            LastExprErrorSecondsAgo = _lastExprErrorAt == DateTime.MinValue
                ? -1 : (Clock() - _lastExprErrorAt).TotalSeconds,
        };
        if (_settings.MetaRules != null)
            lock (_settings.MetaRulesLock)
            {
                s.RuleCount = _settings.MetaRules.Count;
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var r in _settings.MetaRules) seen.Add(r.State ?? "");
                s.StateCount = seen.Count;
            }
        return s;
    }

}

/// <summary>Plain DTO — serialized into the meta bridge JSON for the panel.</summary>
internal sealed class MetaSnapshot
{
    public bool   MacroRunning;
    public bool   MetaEnabled;
    public string CurrentState = "";
    public double SecondsInState;
    public int    StackDepth;
    public int    RuleCount;
    public int    StateCount;
    public bool   WatchdogActive;
    public string WatchdogState = "";
    public double WatchdogSecondsRemaining;
    public string LastFiredState = "";
    public string LastFiredCondition = "";
    public string LastFiredAction = "";
    public double LastFiredSecondsAgo = -1;
    public string LastExprError = "";
    public double LastExprErrorSecondsAgo = -1;
}

// KitRunner.cs - "Make kits" (the Crafting tab's Kits sub-tab, /ra kits): makes Plentiful Healing
// Kits from their ingredients, one combine at a time. KitPlanner picks each combine from a fresh
// look at the pack; this does it and waits for the server.
//
// Pump thread only (the tab's Render reads the volatile snapshots and posts Start/Stop).
// Only while the macro is OFF: Start refuses with the macro on, and turning it on stops the run
// (the macro's own heals, buffs and fights would race the combines). Each step:
//   1. stop checks: the macro turned on, out of peace mode, health dropped (something hit you), or
//      the character moved from where the run started;
//   2. gates: not busy, ActionPacer;
//   3. KitPlanner.Next over the pack (the floor: Min chance %, checked ahead from the buffed skill);
//   4. arm the engine's one-shot answer for AC's chance prompt (v85: Yes at the floor, else No;
//      it goes before the Auto-confirm crafting arm) and UseObjectOn(source, target);
//   5. wait for the result: the recipe's chat line, the product showing up or the parts going, a
//      declined prompt, "cannot be used on", or a WeenieError (YoureTooBusy retries).
// The server only asks when the character option "show the chance of success dialog" is on;
// without it the combine just happens, so the floor is checked here first, every step.
// Ends with one plain chat line: made, failed, lost, and why it stopped.
//
// "Make kits when low" (KitAuto, RynthAiPlugin.Kits.cs) starts runs with StartAuto: the same
// combines, only while the macro is ON (it holds the bot meanwhile); the macro turning off stops it,
// AC's own craft dialog stops it (nobody is there to click), and each stop says what kind it was
// (KitStopKind) so the waits after it follow. Start (the button, /ra kits make) is unchanged.
using System;
using System.Collections.Generic;
using System.Linq;
using RynthCore.Plugin.Shared;
using RynthCore.PluginSdk;

namespace RynthCore.Plugin.RynthAi.MiniRemote.Crafting;

internal sealed class KitRunner
{
    private const uint ErrorYoureTooBusy = 0x1D;
    private const long StepTimeoutMs = 15_000;
    /// <summary>The player answers AC's own dialog: give them time.</summary>
    private const long ManualTimeoutMs = 60_000;
    private const long RetryDelayMs = 1_500;
    private const long StepGapMs = 600;
    /// <summary>After the parts went, how long to wait for the product (or the result line) before calling it a failure.</summary>
    private const long ProductGraceMs = 1_500;
    private const int MaxRetries = 3;
    /// <summary>Moving more than this from the start (same cell) stops the run.</summary>
    public const float MoveTolerance = 1.5f;
    /// <summary>The plan the tab shows is refreshed this often while the tab is drawn.</summary>
    private const long PlanRefreshMs = 1_000;
    private const long ViewedWindowMs = 2_500;

    private readonly MiniRemoteContext _ctx;
    private readonly IReadOnlyList<KitRecipe> _recipes;

    // Job (pump thread)
    private KitRecipe? _recipe;
    private int _limit;
    private int _made, _failed, _retries;
    private readonly Dictionary<string, int> _lost = new(StringComparer.Ordinal);
    private bool _waiting, _manual;
    private KitCombine? _step;
    private int _confSeq, _errSeq;
    private long _stepAt, _nextAt, _partsGoneAt;
    private int _srcBefore, _tgtBefore, _productBefore;
    private bool _havePose;
    private uint _startCell;
    private float _startX, _startY, _startZ;
    private uint _startHealth;
    /// <summary>A "Make kits when low" run (StartAuto); its end goes to <see cref="_autoEnded"/>.</summary>
    private bool _auto;
    private Func<KitStopKind, int, string>? _autoEnded;

    // Render snapshots
    private volatile bool _active;
    private volatile string _status = "idle";
    private volatile string _lastSummary = string.Empty;
    private volatile KitPlanRow[] _plan = Array.Empty<KitPlanRow>();
    private long _planAt = long.MinValue / 2;
    private long _viewedAt = long.MinValue / 2;

    public KitRunner(MiniRemoteContext ctx, IReadOnlyList<KitRecipe>? recipes = null)
    {
        _ctx = ctx;
        _recipes = recipes ?? KitPlanner.Retail;
    }

    public bool Running => _active;
    public string Status => _status;
    /// <summary>The last run's closing line ("" before one ends).</summary>
    public string LastSummary => _lastSummary;
    /// <summary>What the pack makes (refreshed while the tab is drawn, and by /ra kits).</summary>
    public KitPlanRow[] Plan => _plan;
    public IReadOnlyList<KitRecipe> Recipes => _recipes;
    public KitRecipe? Recipe => _recipe;
    public int MadeCount => _made;
    public int FailedCount => _failed;
    /// <summary>The run going was started by "Make kits when low" (StartAuto).</summary>
    public bool Auto => _active && _auto;
    /// <summary>The run's count (0 = as many as the pack makes).</summary>
    public int Limit => _limit;
    /// <summary>How the last run ended.</summary>
    public KitStopKind LastStopKind { get; private set; } = KitStopKind.Other;

    private RynthCoreHost Host => _ctx.Host;
    private MiniRemoteInventory Inv => _ctx.Inventory;
    private CraftingTabState C => _ctx.State.Crafting;
    public int Floor => Math.Clamp(C.KitMinChancePercent, 0, 100);

    /// <summary>The tab was drawn (render thread): keep the plan fresh.</summary>
    public void MarkViewed(long nowMs) => System.Threading.Volatile.Write(ref _viewedAt, nowMs);

    // ── Pack and skill ──────────────────────────────────────────────────────

    /// <summary>The pack's stacks of anything a recipe uses or makes (equipped items left out).</summary>
    public List<KitStack> Pack()
    {
        var wanted = new HashSet<uint>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in _recipes)
            foreach (var st in r.Steps)
                foreach (var p in new[] { st.Source, st.Target, st.Product })
                {
                    wanted.Add(p.Wcid);
                    names.Add(p.Name);
                }
        var list = new List<KitStack>();
        foreach (var wo in Inv.Items())
        {
            string name = wo.Name ?? string.Empty;
            uint wcid = Inv.Wcid(wo);
            if (wcid != 0 ? !wanted.Contains(wcid) : !names.Contains(name)) continue;
            if (Inv.IsEquipped(wo)) continue;
            list.Add(new KitStack(wo.Id, wcid, name, Inv.StackSize(wo)));
        }
        return list;
    }

    /// <summary>The player's buffed skill and training (Unknown when the engine can't read it).</summary>
    public KitSkill Skill(int skillId)
    {
        if (!Host.HasGetObjectSkill) return KitSkill.Unknown;
        if (!Host.TryGetObjectSkill(Inv.PlayerId, unchecked((uint)skillId), out int buffed, out int training)) return KitSkill.Unknown;
        // The engine reads 0 for a trained skill before the qualities arrive: unknown, not "can't".
        if (training >= 2 && buffed <= 0) return KitSkill.Unknown;
        return new KitSkill(true, buffed, training);
    }

    /// <summary>Re-reads the pack into <see cref="Plan"/> (pump thread).</summary>
    public KitPlanRow[] RefreshPlan()
    {
        var rows = KitPlanner.Plan(_recipes, Pack(), Skill, Floor).ToArray();
        _plan = rows;
        _planAt = Environment.TickCount64;
        return rows;
    }

    public KitRecipe? Find(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return _recipes.FirstOrDefault();
        string t = id.Trim();
        return _recipes.FirstOrDefault(r => r.Id.Equals(t, StringComparison.OrdinalIgnoreCase)
                                            || r.Name.Equals(t, StringComparison.OrdinalIgnoreCase));
    }

    // ── Job control ─────────────────────────────────────────────────────────

    /// <summary>
    /// Why a run can't start now ("" when it can): the macro is on, not in the world, not in
    /// peace mode, or the pack makes none.
    /// </summary>
    public string CanStart(KitRecipe recipe)
    {
        if (Running) return "a kit run is already going (Stop it first)";
        var s = _ctx.Settings;
        if (s == null) return "not ready yet (log in first)";
        if (s.IsMacroRunning) return "turn the macro off first (kits are made only while it is off)";
        if (Inv.PlayerId == 0) return "not in the world yet";
        if (!Inv.InPeaceMode) return "go to peace mode first";
        var next = KitPlanner.Next(recipe, Pack(), Skill, Floor);
        return next.Step == null ? next.Why : string.Empty;
    }

    /// <summary>Starts making <paramref name="recipe"/> (limit 0 = as many as the pack makes). Returns "" or why not. Pump thread.</summary>
    public string Start(KitRecipe recipe, int limit)
    {
        string why = CanStart(recipe);
        if (why.Length > 0) { _status = why; return why; }
        Begin(recipe, limit, auto: false, ended: null);
        return string.Empty;
    }

    /// <summary>
    /// Why a "when low" run can't start now ("" when it can): another run is going, the macro is off,
    /// not in the world, not in peace mode, or the pack makes none at the floor.
    /// </summary>
    public string CanStartAuto(KitRecipe recipe)
    {
        if (Running) return "a kit run is already going";
        var s = _ctx.Settings;
        if (s == null) return "not ready yet (log in first)";
        if (!s.IsMacroRunning) return "the macro is off";
        if (Inv.PlayerId == 0) return "not in the world yet";
        if (!Inv.InPeaceMode) return "not in peace mode";
        var next = KitPlanner.Next(recipe, Pack(), Skill, Floor);
        return next.Step == null ? next.Why : string.Empty;
    }

    /// <summary>
    /// "Make kits when low": starts a run with the macro on (up to <paramref name="limit"/> kits).
    /// <paramref name="ended"/> hears how it ended and the kits made, and returns what the summary
    /// line adds (the next try). Returns "" or why not. Pump thread.
    /// </summary>
    public string StartAuto(KitRecipe recipe, int limit, Func<KitStopKind, int, string>? ended)
    {
        string why = CanStartAuto(recipe);
        if (why.Length > 0) return why;
        Begin(recipe, limit, auto: true, ended: ended);
        return string.Empty;
    }

    private void Begin(KitRecipe recipe, int limit, bool auto, Func<KitStopKind, int, string>? ended)
    {
        _auto = auto;
        _autoEnded = ended;
        _recipe = recipe;
        _limit = Math.Max(0, limit);
        _made = _failed = _retries = 0;
        _lineOwedUntil = 0;
        _lost.Clear();
        _waiting = false;
        _nextAt = 0;
        _havePose = Host.TryGetPlayerPose(out _startCell, out _startX, out _startY, out _startZ, out _, out _, out _, out _);
        _startHealth = Inv.PlayerHealth().Cur;
        _active = true;
        int can = KitPlanner.CanMake(recipe, Pack());
        int target = _limit > 0 ? Math.Min(_limit, can) : can;
        _status = $"making {target} {recipe.Name}(s)" + (auto ? " (Make kits when low)" : "");
        RynthLog.Write(LogCat.Crafting, $"[Kits] start{(auto ? " (when low)" : "")}: {recipe.Name}, up to {target} (pack makes {can}), floor {Floor}%");
    }

    /// <summary>
    /// Ends the run and prints the summary (pump thread). No-op when idle. <paramref name="kind"/>:
    /// how it ended (the player's Stop unless said), for the "when low" waits.
    /// </summary>
    public void Stop(string why, KitStopKind kind = KitStopKind.User)
    {
        if (!_active) { _status = why; return; }
        if (_waiting && Host.HasConfirmationAnswers) Host.ArmConfirmation(RynthCoreHost.ConfirmationKind.CraftInteraction, 0, 0);
        _waiting = false;
        _active = false;
        LastStopKind = kind;
        bool auto = _auto;
        var ended = _autoEnded;
        _auto = false;
        _autoEnded = null;
        string tail = string.Empty;
        if (auto && ended != null)
        {
            try { tail = ended(kind, _made) ?? string.Empty; }
            catch (Exception ex) { RynthLog.Exception(LogCat.Crafting, ex, "Make kits when low: the end of a run"); }
        }
        string name = _recipe?.Name ?? "kit";
        string line = KitPlanner.Summary(name, _made, _failed, _lost, why) + tail;
        _lastSummary = line;
        _status = "idle - " + why;
        _ctx.Chat((auto ? "[RynthAi] Make kits when low: " : "[RynthAi] Make kits: ") + line);
        RynthLog.Write(LogCat.Crafting, "[Kits] " + (auto ? "(when low) " : "") + line);
        RefreshPlan();
    }

    public void OnLogout()
    {
        Stop("logged out", KitStopKind.Other);
        _plan = Array.Empty<KitPlanRow>();
        _status = "idle";
    }

    // ── Tick (pump thread) ──────────────────────────────────────────────────

    /// <summary>The last tick's clock (the result handlers time the next step from it).</summary>
    private long _now;

    public void Tick(long nowMs)
    {
        _now = nowMs;
        if (!Running)
        {
            long viewed = System.Threading.Volatile.Read(ref _viewedAt);
            if (nowMs - viewed < ViewedWindowMs && nowMs - _planAt >= PlanRefreshMs) RefreshPlan();
            return;
        }

        var (stop, stopKind) = StopReason();
        if (stop.Length > 0) { Stop(stop, stopKind); return; }

        if (_waiting) { Wait(nowMs); return; }
        if (nowMs < _nextAt) return;
        if (Inv.IsBusy) return;
        if (!ActionPacer.MayStart(Host, "Kits", HealNeed.None, out string pace)) { _status = "waiting: " + pace; return; }

        var recipe = _recipe!;
        if (_limit > 0 && _made >= _limit) { Stop($"made the {_limit} asked for", KitStopKind.Finished); return; }

        var pack = Pack();
        var next = KitPlanner.Next(recipe, pack, Skill, Floor);
        if (next.Step == null) { Stop(next.Why, KitStopKind.Finished); return; }
        var step = next.Step;

        int floor = Floor;
        _manual = !(Host.HasConfirmationAnswers
                    && Host.ArmConfirmation(RynthCoreHost.ConfirmationKind.CraftInteraction, floor, (int)StepTimeoutMs) == 1);
        _confSeq = Host.TryGetLastConfirmation(out int cs, out _, out _, out _, out _) ? cs : 0;
        _errSeq = Host.TryGetLastWeenieError(out int es, out _, out _, out _) ? es : 0;
        _srcBefore = KitPlanner.Count(step.Source, pack);
        _tgtBefore = KitPlanner.Count(step.Target, pack);
        _productBefore = KitPlanner.Count(step.Product, pack);

        string pct = next.Percent >= 0 ? $" ~{next.Percent}%" : "";
        if (!Host.UseOnFor(unchecked((uint)next.SourceId), unchecked((uint)next.TargetId), "Kits",
                $"{step.Label}: {step.Source.Name} on {step.Target.Name}{pct}" + (_auto ? " (Make kits when low)" : ""),
                _auto ? UseKind.Auto : UseKind.Asked))
        {
            if (!_manual) Host.ArmConfirmation(RynthCoreHost.ConfirmationKind.CraftInteraction, 0, 0);
            Retry(nowMs, "the use request didn't go out");
            return;
        }
        _step = step;
        _waiting = true;
        _stepAt = nowMs;
        _partsGoneAt = 0;
        string ask = _manual && !_auto ? " - click Yes if AC asks" : "";
        _status = $"{step.Label}{pct} ({_made} made){ask}";
    }

    /// <summary>
    /// Why the run must stop now ("" to go on), and what kind of stop: the macro (on for a run by
    /// hand, off for a "when low" one), combat mode, damage, moving.
    /// </summary>
    private (string Why, KitStopKind Kind) StopReason()
    {
        var s = _ctx.Settings;
        if (s == null) return ("settings went away", KitStopKind.Other);
        if (_auto)
        {
            if (!s.IsMacroRunning) return ("the macro turned off", KitStopKind.MacroOff);
        }
        else if (s.IsMacroRunning) return ("the macro turned on", KitStopKind.Other);
        if (!Inv.InPeaceMode) return ("you left peace mode", KitStopKind.Danger);
        uint hp = Inv.PlayerHealth().Cur;
        if (_startHealth > 0 && hp > 0 && hp < _startHealth) return ("you took damage", KitStopKind.Danger);
        if (hp > _startHealth) _startHealth = hp;   // healed up: the new level is the line
        if (_havePose && Host.TryGetPlayerPose(out uint cell, out float x, out float y, out float z, out _, out _, out _, out _))
        {
            float dx = x - _startX, dy = y - _startY, dz = z - _startZ;
            if (cell != _startCell || dx * dx + dy * dy + dz * dz > MoveTolerance * MoveTolerance) return ("you moved", KitStopKind.Danger);
        }
        return (string.Empty, KitStopKind.Other);
    }

    private void Retry(long nowMs, string why)
    {
        _waiting = false;
        if (++_retries > MaxRetries) { Stop("gave up: " + why, KitStopKind.Refused); return; }
        _nextAt = nowMs + RetryDelayMs;
        _status = $"{why} - retrying ({_retries}/{MaxRetries})";
    }

    /// <summary>A result the counts showed before its chat line: that line, when it comes, is the old step's.</summary>
    private const long LineOwedMs = 3_000;
    private long _lineOwedUntil;

    private void Succeeded(bool byLine)
    {
        var step = _step!;
        _lineOwedUntil = byLine ? 0 : _now + LineOwedMs;
        _waiting = false;
        _retries = 0;
        _nextAt = _now + StepGapMs;
        bool kit = ReferenceEquals(step, _recipe!.Final);
        if (kit) _made++;
        _status = kit ? $"made {_made} {_recipe.Name}(s)" : $"{step.Product.Name} made";
        RynthLog.Write(LogCat.Crafting, $"[Kits] {step.Label}: ok ({_made} made)");
    }

    private void Failed(bool byLine)
    {
        var step = _step!;
        _lineOwedUntil = byLine ? 0 : _now + LineOwedMs;
        _waiting = false;
        _retries = 0;
        _nextAt = _now + StepGapMs;
        _failed++;
        foreach (var p in new[] { step.Source, step.Target })
            _lost[p.Name] = (_lost.TryGetValue(p.Name, out int n) ? n : 0) + 1;
        _status = $"{step.Label} failed ({_failed} failed so far)";
        RynthLog.Write(LogCat.Crafting, $"[Kits] {step.Label}: failed, lost 1 {step.Source.Name} + 1 {step.Target.Name}");
    }

    private void Wait(long nowMs)
    {
        var step = _step!;
        // Engine answer: a No (the server's own percent under the floor) uses nothing.
        if (Host.TryGetLastConfirmation(out int seq, out uint type, out int pct, out int outcome, out _)
            && seq != _confSeq && type == RynthCoreHost.ConfirmationKind.CraftInteraction)
        {
            _confSeq = seq;
            if (outcome == RynthCoreHost.ConfirmationOutcome.Declined)
            {
                _waiting = false;
                Stop($"the server's chance for {step.Label} is {pct}%, under your {Floor}% floor", KitStopKind.Refused);
                return;
            }
            if (outcome is RynthCoreHost.ConfirmationOutcome.Shown or RynthCoreHost.ConfirmationOutcome.SendFailed)
            {
                if (_auto)
                {
                    // Nobody is there to click it: a "when low" run never waits on AC's dialog.
                    _waiting = false;
                    Stop("AC's craft dialog came up and needs a click (Yes makes this one)", KitStopKind.Refused);
                    return;
                }
                _manual = true;
                _status = $"click Yes in AC's craft dialog ({(pct >= 0 ? pct + "%" : "chance not shown")})";
            }
        }

        if (Host.TryGetLastWeenieError(out int es, out uint error, out _, out _) && es != _errSeq)
        {
            _errSeq = es;
            if (error == ErrorYoureTooBusy) { Retry(nowMs, "server says busy"); return; }
            _waiting = false;
            Stop($"the server refused {step.Label} (error 0x{error:X})", KitStopKind.Refused);
            return;
        }

        // Counts: the product showing up is a success; both parts going without it is a failure.
        var pack = Pack();
        if (KitPlanner.Count(step.Product, pack) > _productBefore) { Succeeded(byLine: false); return; }
        bool partsGone = KitPlanner.Count(step.Source, pack) < _srcBefore && KitPlanner.Count(step.Target, pack) < _tgtBefore;
        if (partsGone)
        {
            if (_partsGoneAt == 0) _partsGoneAt = nowMs;
            else if (nowMs - _partsGoneAt >= ProductGraceMs) { Failed(byLine: false); return; }
        }

        // A "when low" run has no one to answer AC's dialog: the plain timeout, then retries.
        bool waitForPlayer = _manual && !_auto;
        if (nowMs - _stepAt > (waitForPlayer ? ManualTimeoutMs : StepTimeoutMs))
        {
            if (waitForPlayer) { _waiting = false; Stop("no answer to AC's craft dialog", KitStopKind.Refused); }
            else Retry(nowMs, "no result from the server");
        }
    }

    /// <summary>The recipe's result lines and "cannot be used on" (never hidden).</summary>
    public void OnChat(string text)
    {
        if (!_active || string.IsNullOrEmpty(text)) return;
        string t = text.Trim();
        if (_lineOwedUntil != 0 && IsResultLine(t))
        {
            bool owed = _now < _lineOwedUntil;
            _lineOwedUntil = 0;
            if (owed) return;   // the step the counts already finished
        }
        if (!_waiting || _step == null) return;
        if (t.Equals(_step.SuccessText, StringComparison.Ordinal)) { Succeeded(byLine: true); return; }
        if (t.Equals(_step.FailText, StringComparison.Ordinal)) { Failed(byLine: true); return; }
        if (t.StartsWith("The ", StringComparison.Ordinal) && t.Contains(" cannot be used on the ", StringComparison.Ordinal))
        {
            _waiting = false;
            Stop($"this server has no such recipe ({_step.Source.Name} on {_step.Target.Name})", KitStopKind.Refused);
        }
    }

    private bool IsResultLine(string t)
        => _recipe != null && _recipe.Steps.Any(st => t.Equals(st.SuccessText, StringComparison.Ordinal) || t.Equals(st.FailText, StringComparison.Ordinal));
}

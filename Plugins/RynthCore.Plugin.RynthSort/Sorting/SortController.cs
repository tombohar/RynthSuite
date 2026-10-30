using System;
using System.Collections.Generic;
using RynthCore.Plugin.Shared;

namespace RynthCore.Plugin.RynthSort.Sorting;

/// <summary>What the controller needs from the game (the plugin's HostSortWorld; the tests' fake).</summary>
internal interface ISortWorld
{
    /// <summary>The packs now, or null when they can't be read yet.</summary>
    SortInventory? ReadInventory();
    /// <summary>The "stay out of the way" inputs now.</summary>
    GateInputs ReadGate(bool sortInCombat, long nowMs);
    /// <summary>Sends the move to the client (merge, move to a slot, or plain move). False = the engine refused it.</summary>
    bool SendMove(in SortMove move);
    /// <summary>The moved item (its slot too, for a reorder) and the server's last refusal.</summary>
    MoveObservation Observe(in SortMove move);
    /// <summary>The engine's refusal counter now (0 when the engine can't say).</summary>
    int ErrorSeqNow();
    /// <summary>What this engine lets the planner do beyond moving items to their packs.</summary>
    SortAbilities Abilities { get; }
}

/// <summary>What the engine can do for RynthSort (the plugin asks the host; the tests say).</summary>
internal struct SortAbilities
{
    /// <summary>Slot orders are readable (API v80 GetContainerOrder).</summary>
    public bool ReadOrder;
    /// <summary>A move can name its slot (API v80 PutItemInContainer, bound on this client).</summary>
    public bool PlaceMoves;
    /// <summary>Stacks can be merged (MergeStackInternal).</summary>
    public bool Merge;

    public static SortAbilities All => new() { ReadOrder = true, PlaceMoves = true, Merge = true };
}

/// <summary>
/// The sorting loop, one step per plugin tick: one move at a time, each waited on until the
/// server answers it (the item lands, or it is refused, or the time limit passes), then a gap.
/// Auto-sort keeps every pack tidy (<see cref="SortScope.All"/>): new main-pack items go to their
/// packs after the settle time, misplaced side-pack items to theirs, then stacks are joined and
/// items put in order inside each pack, one move per gap; a job asked for (Sort now, Clean,
/// Unpack) goes as fast as the server answers. Nothing is sent while the
/// gate says why not (<see cref="SortGate"/>). An item that fails is left alone for a minute, and
/// failures in a row hold every move off longer and longer (<see cref="MoveTracker.BackoffMs"/>).
/// </summary>
internal sealed class SortController
{
    /// <summary>Between two moves of a job asked for (the server's answer came already).</summary>
    public const long JobGapMs = 100;
    /// <summary>How often the gate is checked while paused (and auto-sort off is looked at).</summary>
    public const long PollMs = 250;
    /// <summary>How often the packs are read while nothing moves (and for the window while paused).</summary>
    public const long ViewMs = 1000;
    public const int LogLines = 6;

    public CharacterSortData Data = new();
    public readonly SortPlanner Planner = new();
    public readonly SortGate Gate = new();

    /// <summary>"watching", "off", "sorting", "unpacking", "paused: ..."; shown beside the auto-sort box.</summary>
    public string Status { get; private set; } = "starting";
    public bool Paused => Status.StartsWith("paused", StringComparison.Ordinal);
    /// <summary>A one-off job asked for, or null.</summary>
    public SortScope? Job { get; private set; }
    /// <summary>The side pack Unpack empties (null = all).</summary>
    public int? UnpackWhich { get; private set; }
    /// <summary>RynthAi's bits from the last gate read (null = RynthAi not there or not ready).</summary>
    public RynthAiActivity? LastBot { get; private set; }
    /// <summary>The last few moves and job results, newest first.</summary>
    public readonly List<string> Log = new();
    /// <summary>What the engine allowed at the last tick (the window says when ordering isn't possible).</summary>
    public SortAbilities Abilities { get; private set; }
    public int Moved { get; private set; }
    public int Failed { get; private set; }

    /// <summary>Settings changed by a command (auto-sort off for Unpack): the plugin saves.</summary>
    public Action? SettingsChanged;
    /// <summary>The one-time pack fix ran (<see cref="PackFix"/>): its lines and how many rules it changed. The plugin logs and saves.</summary>
    public Action<List<string>, int>? PacksFixed;

    /// <summary>How long the side-slot layout must read the same before the one-time pack fix runs.</summary>
    public const long FixSettleMs = 3000;

    private MoveInFlight? _inFlight;
    private long _nextStepMs;
    private long _nextViewMs;
    private int _failuresInARow;
    private string? _fixLayout;
    private long _fixLayoutSince;

    public bool HasMoveOut => _inFlight != null;

    /// <summary>Forget the character (logout, another character's data).</summary>
    public void Reset()
    {
        Planner.Reset();
        Gate.Reset();
        _inFlight = null;
        _nextStepMs = _nextViewMs = 0;
        _failuresInARow = 0;
        _fixLayout = null;
        Job = null;
        UnpackWhich = null;
        LastBot = null;
        Status = "starting";
        Log.Clear();
        Moved = Failed = 0;
    }

    // ── Asking ──────────────────────────────────────────────────────────────

    public void SetAuto(bool on)
    {
        Data.Settings.Auto = on;
        SettingsChanged?.Invoke();
    }

    /// <summary>Sort now (Main), Tidy (All) or Clean, as soon as the way is clear.</summary>
    public void StartJob(SortScope scope)
    {
        if (scope == SortScope.Unpack) { StartUnpack(null); return; }
        Job = scope;
        _nextStepMs = 0;
    }

    /// <summary>Empty every side pack (or pack <paramref name="which"/>) into the main pack; auto-sort goes off first so nothing moves straight back.</summary>
    public void StartUnpack(int? which)
    {
        Data.Settings.Auto = false;
        Job = SortScope.Unpack;
        UnpackWhich = which;
        _nextStepMs = 0;
        SettingsChanged?.Invoke();
    }

    // Items put in order in one pack go in the log as one line that counts up.
    private string? _reorderRunLine;
    private int _reorderRunPack = -1, _reorderRunCount;

    private void NoteReorderRun(in SortMove m)
    {
        if (_reorderRunLine != null && _reorderRunPack == m.DestIndex && Log.Count > 0 && ReferenceEquals(Log[0], _reorderRunLine))
        {
            _reorderRunCount++;
            Log[0] = _reorderRunLine = $"{_reorderRunCount} items put in order in {m.DestLabel}";
            return;
        }
        _reorderRunPack = m.DestIndex;
        _reorderRunCount = 1;
        string name = m.Item.Name.Length > 0 ? m.Item.Name : $"0x{m.Item.Id:X8}";
        Note($"{name} {m.Done}");
        _reorderRunLine = Log[0];
    }

    public void Note(string line)
    {
        Log.Insert(0, line);
        while (Log.Count > LogLines) Log.RemoveAt(Log.Count - 1);
    }

    // ── The loop ────────────────────────────────────────────────────────────

    public void Tick(ISortWorld world, long nowMs)
    {
        Abilities = world.Abilities;
        if (_inFlight != null)
        {
            MoveObservation o = world.Observe(in _inFlight.Move);
            MoveOutcome outcome = MoveTracker.Observe(_inFlight, in o, nowMs);
            if (outcome == MoveOutcome.Pending) return;
            Finish(_inFlight.Move, outcome, nowMs);
            _inFlight = null;
            return;
        }
        if (nowMs < _nextStepMs) return;

        SortScope? scope = Job ?? (Data.Settings.Auto ? SortScope.All : null);
        if (scope == null)
        {
            Status = "off";
            LastBot = null;
            RefreshView(world, SortScope.All, nowMs);
            _nextStepMs = nowMs + PollMs;
            return;
        }

        GateInputs g = world.ReadGate(Data.Settings.SortInCombat, nowMs);
        LastBot = g.Bot;
        string? why = Gate.Evaluate(in g, nowMs);
        if (why != null)
        {
            Status = "paused: " + why;
            RefreshView(world, scope == SortScope.Unpack ? SortScope.Main : scope.Value, nowMs);
            _nextStepMs = nowMs + PollMs;
            return;
        }

        SortInventory? inv = world.ReadInventory();
        if (inv == null)
        {
            Status = "waiting for the inventory";
            _nextStepMs = nowMs + ViewMs;
            return;
        }
        _nextViewMs = nowMs + ViewMs;
        if (!AfterRead(inv, nowMs))
        {
            // Rules not tied to their packs yet: nothing moves by a number that may be wrong.
            Status = "reading your packs";
            Planner.Plan(inv, Data.Rules, SortScope.Main, force: false, nowMs, Data.Settings.SettleSec * 1000, Options(world));
            _nextStepMs = nowMs + PollMs;
            return;
        }
        List<SortMove> moves = scope == SortScope.Unpack
            ? Planner.PlanUnpack(inv, UnpackWhich, nowMs)
            : Planner.Plan(inv, Data.Rules, scope.Value, force: Job != null, nowMs, Data.Settings.SettleSec * 1000, Options(world));

        if (moves.Count == 0)
        {
            if (Job != null) NoteJobDone(Job.Value);
            Job = null;
            UnpackWhich = null;
            Status = Data.Settings.Auto ? "watching" : "off";
            _nextStepMs = nowMs + ViewMs;   // nothing to do: look again in a second (the Lua script's pace)
            return;
        }

        Status = scope == SortScope.Unpack ? "unpacking" : "sorting";
        SortMove m = moves[0];
        int seq = world.ErrorSeqNow();
        int stack = m.Item.Stack;   // as read before the send (a merge's answer is a smaller stack)
        if (!world.SendMove(in m))
        {
            Finish(m, MoveOutcome.Refused, nowMs, "the engine refused the move");
            return;
        }
        _inFlight = new MoveInFlight { Move = m, SentMs = nowMs, ErrorSeqAtSend = seq, StackAtSend = stack };
    }

    /// <summary>What the planner may do: the engine's abilities and the settings.</summary>
    internal PlanOptions Options(ISortWorld world)
    {
        SortAbilities a = world.Abilities;
        return new PlanOptions
        {
            Order = a.ReadOrder && a.PlaceMoves && Data.Settings.OrderInPacks,
            KindFirst = Data.Settings.OrderKindFirst,
            Place = a.ReadOrder && a.PlaceMoves,
            Merge = a.Merge,
        };
    }

    private void NoteJobDone(SortScope job)
    {
        SortView v = Planner.View;
        switch (job)
        {
            case SortScope.Unpack: Note(v.MainFull ? "Main pack is full: unpacking stopped" : "Side packs emptied"); break;
            case SortScope.Clean: Note(v.MainFull ? "Main pack is full: some strays left" : "Packs cleaned"); break;
            case SortScope.All: Note(v.Waiting > 0 ? $"Packs sorted ({v.Waiting} can't move yet)" : "Packs sorted"); break;
            default: Note(v.Waiting > 0 ? $"Main pack sorted ({v.Waiting} can't move yet)" : "Main pack sorted"); break;
        }
    }

    private void Finish(SortMove m, MoveOutcome outcome, long nowMs, string? why = null)
    {
        string name = m.Item.Name.Length > 0 ? m.Item.Name : $"0x{m.Item.Id:X8}";
        long gap = Job != null ? JobGapMs : Data.Settings.GapMs;
        switch (outcome)
        {
            case MoveOutcome.Done:
                Moved++;
                _failuresInARow = 0;
                if (m.Kind == MoveKind.Reorder)
                {
                    Planner.NoteReorder(m.Item.Id, nowMs);
                    NoteReorderRun(m);
                }
                else
                {
                    _reorderRunLine = null;
                    Note($"{name} {m.Done}");
                }
                _nextStepMs = nowMs + gap;
                return;
            case MoveOutcome.TooBusy:
                // Not the item's fault: no block, just a longer hold.
                _failuresInARow++;
                Note($"{name}: the server is busy, trying again");
                _nextStepMs = nowMs + Math.Max(gap, MoveTracker.BackoffMs(_failuresInARow));
                return;
            default:
                Failed++;
                _failuresInARow++;
                Planner.Block(m.Item.Id, nowMs + MoveTracker.BlockMs);
                why ??= outcome == MoveOutcome.TimedOut ? "no answer from the server" : "the server refused it";
                Note($"{name}: not moved ({why})");
                _nextStepMs = nowMs + Math.Max(gap, MoveTracker.BackoffMs(_failuresInARow));
                return;
        }
    }

    /// <summary>
    /// After each read: the one-time pack fix once the side slots have read the same for
    /// <see cref="FixSettleMs"/> (a pack whose capacity isn't readable yet could pass for a focus),
    /// then each rule's number kept on its pack. False while imported rules still wait for the fix
    /// (their numbers may name the wrong pack); rules saved by RynthSort meant what they say and
    /// sort on meanwhile.
    /// </summary>
    private bool AfterRead(SortInventory inv, long nowMs)
    {
        if (!Data.PacksFixed)
        {
            bool hold = Data.ImportedFrom.Length > 0;
            if (!PackFix.Ready(inv)) { _fixLayout = null; return !hold; }
            string layout = PackFix.Layout(inv);
            if (layout != _fixLayout) { _fixLayout = layout; _fixLayoutSince = nowMs; return !hold; }
            if (nowMs - _fixLayoutSince < FixSettleMs) return !hold;
            List<string> lines = PackFix.Apply(Data.Rules, inv, hold, Data.LuaRules, out int changed);
            Data.PacksFixed = true;
            Data.LuaRules = null;
            if (hold) Note($"Imported pack numbers fixed: {changed} of {lines.Count} rules");
            PacksFixed?.Invoke(lines, changed);
        }
        if (PackFix.Sync(Data.Rules, inv)) SettingsChanged?.Invoke();
        return true;
    }

    /// <summary>Re-reads the packs for the window now and then while nothing moves.</summary>
    private void RefreshView(ISortWorld world, SortScope scope, long nowMs)
    {
        if (nowMs < _nextViewMs) return;
        _nextViewMs = nowMs + ViewMs;
        SortInventory? inv = world.ReadInventory();
        if (inv == null) return;
        AfterRead(inv, nowMs);
        if (scope == SortScope.Unpack) Planner.PlanUnpack(inv, UnpackWhich, nowMs);
        else Planner.Plan(inv, Data.Rules, scope, force: false, nowMs, Data.Settings.SettleSec * 1000, Options(world));
    }
}

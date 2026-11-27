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

    /// <summary>
    /// What RynthAi says about the items for the jobs <paramref name="wanted"/> (<see cref="PackJobs"/>):
    /// the plugin asks RynthAi.Hub. Null = nothing known (the jobs' packs then stay as they are).
    /// </summary>
    JobAnswers? AskJobs(SortInventory inv, IReadOnlyList<JobKind> wanted, string mule, long nowMs) => null;
}

/// <summary>
/// One line of the window's activity feed: what happened, the item it was about (0 = none: a sort,
/// a setting), and the wall-clock time it happened ("14:05").
/// </summary>
internal readonly record struct FeedLine(string Text, uint ItemId, string Time);

/// <summary>An item the player sent to a pack from the window ("Just move it", "Send to"), waiting for the way to clear.</summary>
internal readonly record struct PendingSend(uint ItemId, uint DestId, long UntilMs);

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
    /// <summary>What the player touched lately (left alone for <see cref="HoldMs"/>). The plugin adds the selection and drags.</summary>
    public readonly TouchWatch Touch = new();

    /// <summary>How long an item the player touched is left alone (the "leave items I use alone" setting; 0 = off).</summary>
    public long HoldMs => Math.Max(0, Data.Settings.HoldMin) * 60_000L;
    /// <summary>The packs as last read (the window's preview plans from it), or null before the first read.</summary>
    public SortInventory? LastInventory { get; private set; }
    /// <summary>The time of the last tick (ms).</summary>
    public long Clock { get; private set; }

    public SortController()
    {
        Planner.Held = id => Touch.IsHeld(id, Clock, HoldMs);
    }

    /// <summary>Whether the player touched <paramref name="itemId"/> recently enough that it is left alone.</summary>
    public bool IsHeld(uint itemId) => Touch.IsHeld(itemId, Clock, HoldMs);

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
    /// <summary>The window's activity feed, newest first (<see cref="FeedMax"/> lines): <see cref="Log"/>'s lines with their times, and a few more (auto-sort on and off).</summary>
    public readonly List<FeedLine> Feed = new();
    public const int FeedMax = 12;
    /// <summary>The wall clock the feed's times come from (the tests set it).</summary>
    public Func<DateTime> WallClock = () => DateTime.Now;
    /// <summary>Bumped whenever the feed changes (the window rebuilds its view then).</summary>
    public int FeedVersion { get; private set; }
    /// <summary>What the engine allowed at the last tick (the window says when ordering isn't possible).</summary>
    public SortAbilities Abilities { get; private set; }
    public int Moved { get; private set; }
    public int Failed { get; private set; }

    /// <summary>
    /// The last sort's moves to other packs, oldest first, for "Put back": a run of moves, a new run
    /// starting when a move lands more than <see cref="RunGapMs"/> after the one before. Kept
    /// <see cref="HistoryKeepMs"/> at most. Joins into stacks aren't kept (the item is gone).
    /// </summary>
    public readonly List<MadeMove> History = new();
    public const long RunGapMs = 60_000;
    public const long HistoryKeepMs = 30 * 60_000;
    public const int HistoryMax = 250;
    private int _putBackDone, _putBackPlanned;

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
    /// <summary>The item being moved now (0 = none): the window lifts its tile.</summary>
    public uint InFlightItem => _inFlight?.Move.Item.Id ?? 0;

    // "Just move it" / "Send to" from the window: one item to one pack, then left alone.
    private PendingSend? _send;
    private bool _sendInFlight;
    /// <summary>How long a send waits for the way to clear before it is dropped.</summary>
    public const long SendWaitMs = 30_000;
    /// <summary>An item the player sent from the window that hasn't gone yet, or null.</summary>
    public PendingSend? Send => _send;

    /// <summary>Forget the character (logout, another character's data).</summary>
    public void Reset()
    {
        Planner.Reset();
        Gate.Reset();
        Touch.Reset();
        LastInventory = null;
        _inFlight = null;
        _nextStepMs = _nextViewMs = 0;
        _failuresInARow = 0;
        _fixLayout = null;
        Job = null;
        UnpackWhich = null;
        LastBot = null;
        Status = "starting";
        Log.Clear();
        Feed.Clear();
        FeedVersion++;
        _send = null;
        _sendInFlight = false;
        History.Clear();
        _noFitSaid.Clear();
        _jobs = null;
        Moved = Failed = 0;
    }

    // ── Asking ──────────────────────────────────────────────────────────────

    public void SetAuto(bool on)
    {
        bool was = Data.Settings.Auto;
        Data.Settings.Auto = on;
        if (was != on) NoteFeed(on ? "Auto-sort on" : "Auto-sort off");
        SettingsChanged?.Invoke();
    }

    /// <summary>
    /// "Just move it" / "Send to" (the window): item <paramref name="itemId"/> goes to the pack
    /// <paramref name="destId"/> names (the player's id: the main pack) as soon as the way is clear, by
    /// itself, and is left alone afterwards for the hold time so no sort pulls it back. No rule changes.
    /// It is left alone from now on too, so auto-sort doesn't move it meanwhile. Replaces a send not
    /// made yet. False for no item or no pack.
    /// </summary>
    public bool StartSend(uint itemId, uint destId)
    {
        if (itemId == 0 || destId == 0) return false;
        _send = new PendingSend(itemId, destId, Clock + SendWaitMs);
        Touch.Hold(itemId, Clock, TouchKind.Sent);
        _nextStepMs = Math.Min(_nextStepMs, Clock);
        return true;
    }

    /// <summary>Sort now (Main), Tidy (All) or Clean, as soon as the way is clear.</summary>
    public void StartJob(SortScope scope)
    {
        if (scope == SortScope.Unpack) { StartUnpack(null); return; }
        if (scope == SortScope.PutBack) { StartPutBack(); return; }
        Job = scope;
        _nextStepMs = 0;
    }

    /// <summary>
    /// "Put back": the items the last sort moved go back where they were. Auto-sort goes off first
    /// (it would move them straight back); false when there is nothing to put back.
    /// </summary>
    public bool StartPutBack()
    {
        if (History.Count == 0) return false;
        Data.Settings.Auto = false;
        Job = SortScope.PutBack;
        UnpackWhich = null;
        _putBackDone = 0;
        _putBackPlanned = History.Count;
        _nextStepMs = 0;
        SettingsChanged?.Invoke();
        return true;
    }

    /// <summary>How many items Put back would try to move back now.</summary>
    public int PutBackCount => History.Count;

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
            if (Feed.Count > 0)
            {
                Feed[0] = Feed[0] with { Text = $"{_reorderRunCount} items put in order in {PackLabel(m.DestIndex)}", ItemId = 0 };
                FeedVersion++;
            }
            return;
        }
        _reorderRunPack = m.DestIndex;
        _reorderRunCount = 1;
        string name = m.Item.Name.Length > 0 ? m.Item.Name : $"0x{m.Item.Id:X8}";
        Note($"{name} {m.Done}", 0, $"{name} {FeedDone(m)}");
        _reorderRunLine = Log[0];
    }

    /// <summary>
    /// A line for the log (/rsort status, the older window) and the window's feed. The feed can say
    /// it in other words (<paramref name="feed"/>: pack names instead of numbers); null = the same line.
    /// </summary>
    public void Note(string line, uint itemId = 0, string? feed = null)
    {
        Log.Insert(0, line);
        while (Log.Count > LogLines) Log.RemoveAt(Log.Count - 1);
        NoteFeed(feed ?? line, itemId);
    }

    /// <summary>What the feed calls pack <paramref name="index"/> (0 = main pack): its name, with its number when two share it (<see cref="PackNames"/>).</summary>
    internal string PackLabel(int index) => PackNames.Label(LastInventory, index);

    /// <summary>"-> Satchel", "stacked in Satchel", "put in order in main pack": <see cref="SortMove.Done"/> with the pack's name.</summary>
    internal string FeedDone(in SortMove m)
    {
        string where = PackLabel(m.DestIndex);
        return m.Kind switch
        {
            MoveKind.Stack => "stacked in " + where,
            MoveKind.Reorder => "put in order in " + where,
            _ => "-> " + where,
        };
    }

    /// <summary>A line for the window's feed only (not <see cref="Log"/>, which /rsort status and the old window show).</summary>
    public void NoteFeed(string text, uint itemId = 0)
    {
        string time;
        try { time = WallClock().ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture); }
        catch { time = string.Empty; }
        Feed.Insert(0, new FeedLine(text, itemId, time));
        while (Feed.Count > FeedMax) Feed.RemoveAt(Feed.Count - 1);
        FeedVersion++;
    }

    // ── The loop ────────────────────────────────────────────────────────────

    public void Tick(ISortWorld world, long nowMs)
    {
        Clock = nowMs;
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
        if (_send != null && TickSend(world, nowMs)) return;

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
            RefreshView(world, scope is SortScope.Unpack or SortScope.PutBack ? SortScope.Main : scope.Value, nowMs);
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
        Seen(inv, nowMs, world);
        _nextViewMs = nowMs + ViewMs;
        if (!AfterRead(inv, nowMs))
        {
            // Rules not tied to their packs yet: nothing moves by a number that may be wrong.
            Status = "reading your packs";
            Planner.Plan(inv, Data.Rules, SortScope.Main, force: false, nowMs, Data.Settings.SettleSec * 1000, Options(world));
            _nextStepMs = nowMs + PollMs;
            return;
        }
        List<SortMove> moves = scope switch
        {
            SortScope.Unpack => Planner.PlanUnpack(inv, UnpackWhich, nowMs),
            SortScope.PutBack => Planner.PlanPutBack(inv, History, nowMs),
            _ => Planner.Plan(inv, Data.Rules, scope.Value, force: Job != null, nowMs, Data.Settings.SettleSec * 1000, Options(world)),
        };

        NoteNoFit(jobDone: moves.Count == 0 && Job != null);
        if (moves.Count == 0)
        {
            if (Job != null) NoteJobDone(Job.Value);
            if (Job == SortScope.PutBack) History.Clear();   // what couldn't go back is gone or was moved since
            Job = null;
            UnpackWhich = null;
            Status = Data.Settings.Auto ? "watching" : "off";
            _nextStepMs = nowMs + ViewMs;   // nothing to do: look again in a second (the Lua script's pace)
            return;
        }

        Status = scope switch { SortScope.Unpack => "unpacking", SortScope.PutBack => "putting back", _ => "sorting" };
        SortMove m = moves[0];
        int seq = world.ErrorSeqNow();
        int stack = m.Item.Stack;   // as read before the send (a merge's answer is a smaller stack)
        Touch.NoteOwnMove(m.Item.Id, m.Dest.Id, nowMs);
        if (!world.SendMove(in m))
        {
            Finish(m, MoveOutcome.Refused, nowMs, "the engine refused the move");
            return;
        }
        _inFlight = new MoveInFlight { Move = m, SentMs = nowMs, ErrorSeqAtSend = seq, StackAtSend = stack };
    }

    /// <summary>
    /// The window's send (<see cref="StartSend"/>), before anything else: through the same gate as a
    /// sort, one plain move, then the item is held. True when this tick is used up by it.
    /// </summary>
    private bool TickSend(ISortWorld world, long nowMs)
    {
        PendingSend send = _send!.Value;
        if (nowMs > send.UntilMs)
        {
            _send = null;
            Note("A move you asked for was dropped: the way didn't clear in time", send.ItemId);
            return false;
        }
        GateInputs g = world.ReadGate(Data.Settings.SortInCombat, nowMs);
        LastBot = g.Bot;
        string? why = Gate.Evaluate(in g, nowMs);
        if (why != null)
        {
            Status = "paused: " + why;
            _nextStepMs = nowMs + PollMs;
            return true;
        }
        SortInventory? inv = world.ReadInventory();
        if (inv == null)
        {
            Status = "waiting for the inventory";
            _nextStepMs = nowMs + PollMs;
            return true;
        }
        Seen(inv, nowMs, world);
        _send = null;
        SortItem? item = null;
        int from = -1;
        foreach (SortItem it in inv.Main.Items) if (it.Id == send.ItemId) { item = it; from = 0; break; }
        for (int i = 0; item == null && i < inv.Packs.Count; i++)
            foreach (SortItem it in inv.Packs[i].Items) if (it.Id == send.ItemId) { item = it; from = i + 1; break; }
        int to = send.DestId == inv.Main.Id ? 0 : inv.IndexOf(send.DestId);
        if (item == null || !SortPlanner.Sortable(item))
        {
            Note("A move you asked for was dropped: the item isn't in your packs now", send.ItemId);
            return false;
        }
        string name = item.Name.Length > 0 ? item.Name : $"0x{item.Id:X8}";
        if (to == 0 && send.DestId != inv.Main.Id)
        {
            Note($"{name}: not moved (that pack isn't carried now)", item.Id);
            return false;
        }
        Touch.Hold(item.Id, nowMs, TouchKind.Sent);
        if (to == from) return false;   // there already: just left alone
        SortPack dest = to == 0 ? inv.Main : inv.Packs[to - 1];
        int cap = SortPlanner.CapacityOf(dest, to == 0 ? SortPlanner.MainCapacityFallback : SortPlanner.SideCapacityFallback);
        if (dest.Items.Count >= cap)
        {
            Note($"{name}: not moved ({(to == 0 ? "the main pack" : PackLabel(to))} is full)", item.Id);
            return false;
        }
        var m = new SortMove(item, dest, to, 0, -1, MoveKind.Route, from == 0 ? inv.Main.Id : inv.Packs[from - 1].Id, from);
        Status = "sorting";
        int seq = world.ErrorSeqNow();
        Touch.NoteOwnMove(item.Id, dest.Id, nowMs);
        _sendInFlight = true;
        if (!world.SendMove(in m))
        {
            Finish(m, MoveOutcome.Refused, nowMs, "the engine refused the move");
            return true;
        }
        _inFlight = new MoveInFlight { Move = m, SentMs = nowMs, ErrorSeqAtSend = seq, StackAtSend = item.Stack };
        return true;
    }

    /// <summary>What the planner may do: the engine's abilities and the settings.</summary>
    internal PlanOptions Options(ISortWorld world) => OptionsFor(world.Abilities);

    /// <summary>What the planner may do with the engine as it was at the last tick (the window's preview).</summary>
    internal PlanOptions CurrentOptions => OptionsFor(Abilities);

    private PlanOptions OptionsFor(SortAbilities a)
    {
        return new PlanOptions
        {
            Order = a.ReadOrder && a.PlaceMoves && Data.Settings.OrderInPacks,
            KindFirst = Data.Settings.OrderKindFirst,
            Place = a.ReadOrder && a.PlaceMoves,
            Merge = a.Merge,
            Handy = a.ReadOrder && a.PlaceMoves ? HandyItems.For(Data.Settings) : null,
            Jobs = _jobs,
        };
    }

    /// <summary>A chat line (the plugin writes it, with RynthSort's tag): what didn't fit. Null in the tests that don't look.</summary>
    public Action<string>? Warn;

    // The items the last "didn't fit" line named: a line goes out when one not named yet doesn't fit.
    private readonly HashSet<uint> _noFitSaid = new();

    /// <summary>
    /// Items whose pack (and every overflow pack) is full: one chat line and a feed line per sort,
    /// not per tick. Auto-sort says it when an item that wasn't named yet doesn't fit; a job (Sort now,
    /// Clean) says it once, when it ends. An item that fits again is forgotten, so it is named again
    /// if it stops fitting later.
    /// </summary>
    private void NoteNoFit(bool jobDone)
    {
        List<NoFitLine> nf = Planner.View.NoFit;
        if (nf.Count == 0) { _noFitSaid.Clear(); return; }
        var ids = new HashSet<uint>();
        foreach (NoFitLine l in nf) ids.Add(l.Item.Id);
        _noFitSaid.RemoveWhere(id => !ids.Contains(id));
        if (Job != null && !jobDone) return;
        bool fresh = false;
        foreach (uint id in ids) if (!_noFitSaid.Contains(id)) { fresh = true; break; }
        if (!fresh && !jobDone) return;
        foreach (uint id in ids) _noFitSaid.Add(id);
        string text = NoFitText(nf, LastInventory);
        NoteFeed(text);
        Warn?.Invoke(text);
    }

    /// <summary>
    /// "Satchel is full: Black Opal, Fire Opal and 2 more stay where they are." One sentence per
    /// full pack, the pack by name (<see cref="PackNames"/>), at most three item names each.
    /// </summary>
    internal static string NoFitText(IReadOnlyList<NoFitLine> nf, SortInventory? inv)
    {
        var packs = new List<int>();
        foreach (NoFitLine l in nf) if (!packs.Contains(l.Pack)) packs.Add(l.Pack);
        var sb = new System.Text.StringBuilder();
        foreach (int pack in packs)
        {
            var names = new List<string>();
            int count = 0;
            foreach (NoFitLine l in nf)
            {
                if (l.Pack != pack) continue;
                count++;
                string n = l.Item.Name.Length > 0 ? l.Item.Name : $"0x{l.Item.Id:X8}";
                if (names.Count < 3 && !names.Contains(n)) names.Add(n);
            }
            int more = count - names.Count;
            string list = more > 0 ? string.Join(", ", names) + $" and {more} more"
                : names.Count > 1 ? string.Join(", ", names.GetRange(0, names.Count - 1)) + " and " + names[^1] : names[0];
            if (sb.Length > 0) sb.Append(' ');
            string label = PackNames.Label(inv, pack);
            sb.Append(char.ToUpperInvariant(label[0])).Append(label, 1, label.Length - 1)
              .Append(" is full: ").Append(list).Append(count == 1 ? " stays where it is." : " stay where they are.");
        }
        return sb.ToString();
    }

    private void NoteJobDone(SortScope job)
    {
        SortView v = Planner.View;
        switch (job)
        {
            case SortScope.PutBack:
            {
                int left = _putBackPlanned - _putBackDone;
                Note(_putBackDone == 0 ? "Nothing could be put back (gone, or moved since)"
                     : left > 0 ? $"Put back {_putBackDone} items ({left} gone, moved since, or no room)" : $"Put back {_putBackDone} items");
                break;
            }
            case SortScope.Unpack: Note(v.MainFull ? "Main pack is full: unpacking stopped" : "Side packs emptied"); break;
            case SortScope.Clean: Note(v.MainFull ? "Main pack is full: some strays left" : "Packs cleaned"); break;
            case SortScope.All: Note(v.Waiting > 0 ? $"Packs sorted ({v.Waiting} can't move yet)" : "Packs sorted"); break;
            default: Note(v.Waiting > 0 ? $"Main pack sorted ({v.Waiting} can't move yet)" : "Main pack sorted"); break;
        }
    }

    private void Finish(SortMove m, MoveOutcome outcome, long nowMs, string? why = null)
    {
        string name = m.Item.Name.Length > 0 ? m.Item.Name : $"0x{m.Item.Id:X8}";
        bool sent = _sendInFlight;
        _sendInFlight = false;
        long gap = Job != null || sent ? JobGapMs : Data.Settings.GapMs;
        switch (outcome)
        {
            case MoveOutcome.Done when sent:
                // The player's own move from the window: not a sort (Put back leaves it), and left alone from now.
                Moved++;
                _failuresInARow = 0;
                Touch.Hold(m.Item.Id, nowMs, TouchKind.Sent);
                _reorderRunLine = null;
                Note($"{name} {m.Done} (you sent it)", m.Item.Id, $"{name} {FeedDone(m)} (you sent it)");
                _nextStepMs = nowMs + gap;
                return;
            case MoveOutcome.Done:
                Moved++;
                _failuresInARow = 0;
                Remember(m, nowMs);
                if (m.Kind == MoveKind.Reorder)
                {
                    Planner.NoteReorder(m.Item.Id, nowMs);
                    NoteReorderRun(m);
                }
                else
                {
                    _reorderRunLine = null;
                    Note($"{name} {m.Done}", m.Item.Id, $"{name} {FeedDone(m)}");
                }
                _nextStepMs = nowMs + gap;
                return;
            case MoveOutcome.TooBusy:
                // Not the item's fault: no block, just a longer hold.
                _failuresInARow++;
                Note($"{name}: the server is busy, trying again", m.Item.Id);
                _nextStepMs = nowMs + Math.Max(gap, MoveTracker.BackoffMs(_failuresInARow));
                return;
            default:
                Failed++;
                _failuresInARow++;
                Planner.Block(m.Item.Id, nowMs + MoveTracker.BlockMs);
                why ??= outcome == MoveOutcome.TimedOut ? "no answer from the server" : "the server refused it";
                Note($"{name}: not moved ({why})", m.Item.Id);
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

    /// <summary>A move landed: kept for Put back (a move Put back made takes its item off the list instead).</summary>
    private void Remember(in SortMove m, long nowMs)
    {
        if (m.Kind != MoveKind.Route || m.FromId == 0) return;
        if (Job == SortScope.PutBack)
        {
            uint id = m.Item.Id;
            if (History.RemoveAll(h => h.ItemId == id) > 0) _putBackDone++;
            return;
        }
        if (m.MergeInto != 0) return;   // joined a stack: nothing to put back
        if (History.Count > 0 && nowMs - History[^1].AtMs > RunGapMs) History.Clear();   // a new sort
        // Moved twice in one sort: the first place is where it goes back to.
        uint from = m.FromId;
        uint itemId = m.Item.Id;
        int earlier = History.FindIndex(h => h.ItemId == itemId);
        if (earlier >= 0)
        {
            from = History[earlier].FromId;
            History.RemoveAt(earlier);
        }
        if (from == m.Dest.Id) return;   // back where it started
        History.Add(new MadeMove(itemId, m.Item.Name, from, m.Dest.Id, nowMs));
        while (History.Count > HistoryMax) History.RemoveAt(0);
    }

    /// <summary>Old moves fall off the Put back list.</summary>
    private void ForgetOldMoves(long nowMs)
    {
        if (Job == SortScope.PutBack) return;
        while (History.Count > 0 && nowMs - History[0].AtMs > HistoryKeepMs) History.RemoveAt(0);
    }

    /// <summary>
    /// Every read: what the player moved since the last one, the packs for the window's preview, and
    /// RynthAi's answers for the jobs the carried packs have (<see cref="PackJobs"/>).
    /// </summary>
    private void Seen(SortInventory inv, long nowMs, ISortWorld world)
    {
        Touch.Observe(inv, nowMs);
        LastInventory = inv;
        ForgetOldMoves(nowMs);
        List<JobKind> wanted = PackJobs.Wanted(Data.Jobs, inv, out string mule);
        JobAnswers? answers = null;
        if (wanted.Count > 0)
        {
            try { answers = world.AskJobs(inv, wanted, mule, nowMs); }
            catch { answers = null; }   // RynthAi mid-reload: nothing known this time
        }
        _jobs = JobPlan.For(Data.Jobs, inv, answers);
    }

    private JobPlan? _jobs;

    /// <summary>The carried packs' jobs and what RynthAi answered at the last read (null: no pack has a job).</summary>
    public JobPlan? Jobs => _jobs;

    /// <summary>Re-reads the packs for the window now and then while nothing moves.</summary>
    private void RefreshView(ISortWorld world, SortScope scope, long nowMs)
    {
        if (nowMs < _nextViewMs) return;
        _nextViewMs = nowMs + ViewMs;
        SortInventory? inv = world.ReadInventory();
        if (inv == null) return;
        Seen(inv, nowMs, world);
        AfterRead(inv, nowMs);
        if (scope == SortScope.Unpack) Planner.PlanUnpack(inv, UnpackWhich, nowMs);
        else Planner.Plan(inv, Data.Rules, scope, force: false, nowMs, Data.Settings.SettleSec * 1000, Options(world));
    }
}

using System;
using System.Collections.Generic;

namespace RynthCore.Plugin.RynthSort.Sorting;

/// <summary>What a planning pass looks at.</summary>
internal enum SortScope
{
    /// <summary>Only the main pack's items to their packs; an item moves once it has sat there for the settle time (unless forced).</summary>
    Main,
    /// <summary>
    /// Every pack: main-pack items to their packs (after the settle time unless forced), items in the
    /// wrong side pack to theirs, then (with <see cref="PlanOptions"/>) stacks joined and items put
    /// in order inside each pack. Auto-sort ("keep every pack tidy"), and Sort now (forced).
    /// </summary>
    All,
    /// <summary>As <see cref="All"/>, and side-pack items no rule wants go back to the main pack ("Clean").</summary>
    Clean,
    /// <summary>Every side pack (or one) emptied into the main pack ("Unpack").</summary>
    Unpack,
    /// <summary>The items the last sort moved go back to the packs they came from ("Put back").</summary>
    PutBack,
}

/// <summary>A move RynthSort made (an item to another pack), kept so "Put back" can undo it.</summary>
internal readonly record struct MadeMove(uint ItemId, string Name, uint FromId, uint ToId, long AtMs);

/// <summary>What a move does.</summary>
internal enum MoveKind : byte
{
    /// <summary>To another pack (maybe joining a stack there, maybe at a slot).</summary>
    Route,
    /// <summary>Joins another stack of the same kind in its own pack (<see cref="SortMove.MergeInto"/>).</summary>
    Stack,
    /// <summary>To another slot in its own pack (<see cref="SortMove.Placement"/>).</summary>
    Reorder,
}

/// <summary>One move: <see cref="Item"/> into <see cref="Dest"/> (pack <see cref="DestIndex"/>, 0 = main pack).</summary>
internal readonly struct SortMove
{
    public readonly SortItem Item;
    public readonly SortPack Dest;
    public readonly int DestIndex;
    /// <summary>A stack in <see cref="Dest"/> the item joins (a merge), or 0 for a plain move.</summary>
    public readonly uint MergeInto;
    /// <summary>The slot in <see cref="Dest"/> it should end at (counted without it), or -1: the first empty slot.</summary>
    public readonly int Placement;
    public readonly MoveKind Kind;
    /// <summary>The pack the item is in before the move (its object id; the player for the main pack), 0 when not known. "Put back" uses it.</summary>
    public readonly uint FromId;
    /// <summary>The pack it is in before the move: 0 = main pack, else the side pack's number (1-based); -1 when not known.</summary>
    public readonly int FromIndex;

    public SortMove(SortItem item, SortPack dest, int destIndex, uint mergeInto, int placement = -1, MoveKind kind = MoveKind.Route,
                    uint fromId = 0, int fromIndex = -1)
    {
        Item = item;
        Dest = dest;
        DestIndex = destIndex;
        MergeInto = mergeInto;
        Placement = placement;
        Kind = kind;
        FromId = fromId;
        FromIndex = fromIndex;
    }

    /// <summary>The item is already in <see cref="Dest"/> (a stack joined or a slot changed there).</summary>
    public bool InPlace => Kind != MoveKind.Route;

    public string DestLabel => DestIndex == 0 ? "main pack" : "pack " + DestIndex;

    /// <summary>What the log says it did: "-> pack 2", "stacked in pack 2", "put in order in pack 2".</summary>
    public string Done => Kind switch
    {
        MoveKind.Stack => "stacked in " + DestLabel,
        MoveKind.Reorder => "put in order in " + DestLabel,
        _ => "-> " + DestLabel,
    };
}

/// <summary>What a planning pass may do beyond moving items to their packs (what the engine and the settings allow).</summary>
internal struct PlanOptions
{
    /// <summary>Put items in order inside each pack whose slot order is known (API v80 and the "order" setting).</summary>
    public bool Order;
    /// <summary>The order groups by kind (class) first, then name.</summary>
    public bool KindFirst;
    /// <summary>Moves can name a slot (API v80): an item going to a pack whose order is known lands in its place.</summary>
    public bool Place;
    /// <summary>Stacks of the same kind in one pack are joined (the engine can merge stacks).</summary>
    public bool Merge;
    /// <summary>
    /// Handy items (<see cref="HandyItems"/>) are kept in the main pack's first slots: null = off.
    /// Only with <see cref="Place"/> (moves to a slot) and the main pack's slot order known.
    /// </summary>
    public Func<SortItem, bool>? Handy;
    /// <summary>The carried packs' jobs and RynthAi's answers (<see cref="PackJobs"/>): null = no pack has a job.</summary>
    public JobPlan? Jobs;

    /// <summary>Everything on: an engine with API v80 and the default settings.</summary>
    public static PlanOptions Full => new() { Order = true, Place = true, Merge = true };
}

/// <summary>A side pack as the window lists it.</summary>
internal sealed class PackLine
{
    public uint Id;
    public string Name = string.Empty;
    public int Used;
    public int Capacity;
    /// <summary>What is in it, in a few words (<see cref="SortPack.Summary"/>).</summary>
    public string Summary = string.Empty;
    /// <summary>Its slot order is known (it can be put in order).</summary>
    public bool OrderKnown;
    /// <summary>Items out of place in it (0 = in order; only counted when the order is known and ordering is on).</summary>
    public int OutOfPlace;
    /// <summary>Its place in the pack column, 1 = top, foci counted (<see cref="SortPack.ColumnSlot"/>); 0 = not known.</summary>
    public int ColumnSlot;

    /// <summary>"Sack (12/24) - Salvage, Gems".</summary>
    public string Label => $"{Name} ({Used}/{Capacity}) - {Summary}";
    /// <summary>"Sack - Salvage, Gems": what a rule remembers of its pack.</summary>
    public string Note => $"{Name} - {Summary}";
}

/// <summary>A rule whose pack isn't carried: it goes nowhere (<see cref="UsedPack"/> 0) or to the pack its number names.</summary>
internal sealed class RulePackIssue
{
    public SortRule Rule = null!;
    /// <summary>1-based place in the rule list.</summary>
    public int RuleNumber;
    /// <summary>The side pack used instead (its number), or 0 when the rule has none.</summary>
    public int UsedPack;
}

/// <summary>What the window shows about the packs after a planning pass.</summary>
internal sealed class SortView
{
    public readonly List<PackLine> Packs = new();
    public int MainUsed;
    public int MainCapacity;
    /// <summary>The main pack as a line (its order state; Id is the player).</summary>
    public PackLine Main = new();
    /// <summary>The packs are numbered top to bottom as the pack column shows them (else by object id).</summary>
    public bool PackOrderKnown;
    /// <summary>Why the packs are numbered as they are (<see cref="SortInventory.OrderSource"/>).</summary>
    public PackOrderSource OrderSource = PackOrderSource.NoEngine;
    /// <summary>The pass could put items in order (the engine reads slot orders and ordering is on).</summary>
    public bool Ordering;
    /// <summary>Items a rule wants moved that can't go yet (settling, pack full, pack missing, main full).</summary>
    public int Waiting;
    /// <summary>Side packs (1-based) a rule wants to fill that are full.</summary>
    public readonly SortedSet<int> Full = new();
    /// <summary>Pack numbers rules (that are on) use and the character doesn't have.</summary>
    public readonly SortedSet<int> Missing = new();
    /// <summary>Rules (that are on) whose pack isn't carried, in rule order.</summary>
    public readonly List<RulePackIssue> Issues = new();
    /// <summary>Clean or Unpack found the main pack full.</summary>
    public bool MainFull;
    /// <summary>
    /// Items a rule wants in a side pack that is full, with no overflow pack (another pack with the
    /// same rule) that has room: they stay where they are. In the order met, main pack first.
    /// </summary>
    public readonly List<NoFitLine> NoFit = new();
    /// <summary>Handy items are kept at the front of the main pack (the option is on and the engine can do it).</summary>
    public bool Handy;
    /// <summary>Handy items that could move and aren't in the main pack's front slots yet.</summary>
    public int HandyBehind;

    // Why items wait (they add up to Waiting).
    /// <summary>New items in the main pack still inside the settle time.</summary>
    public int WaitSettle;
    /// <summary>Items whose pack is full.</summary>
    public int WaitFull;
    /// <summary>Items whose rule's pack isn't carried.</summary>
    public int WaitNoPack;
    /// <summary>Items Clean or Unpack can't put in the full main pack.</summary>
    public int WaitMainFull;
    /// <summary>How long until the next settling item may move (ms), or -1 when none is settling.</summary>
    public long NextSettleMs = -1;
    /// <summary>The new main-pack items counted in <see cref="WaitSettle"/> (their ids): the window's settling rings.</summary>
    public readonly HashSet<uint> Settling = new();
    /// <summary>Items a rule (or Clean) would move that the player touched lately: left where they are.</summary>
    public readonly List<HeldLine> Held = new();
}

/// <summary>An item left alone because the player touched it lately: in pack <see cref="From"/>, it would go to <see cref="To"/> (0 = main pack).</summary>
internal readonly record struct HeldLine(SortItem Item, int From, int To);

/// <summary>An item that didn't fit: its rule's side pack <see cref="Pack"/> (and every overflow pack) is full; it stays in <see cref="From"/> (0 = main pack).</summary>
internal readonly record struct NoFitLine(SortItem Item, int Pack, int From);

/// <summary>
/// The sorting rules applied to the packs: which items move where, one planning pass at a time.
/// Each item has one place: the pack of the rule it follows (<see cref="SortRules.RuleFor"/>; the
/// pack its id names, else its number: <see cref="SortRules.Resolve"/>), or where it is when no
/// rule wants it (Clean: back to the main pack). Worn and wielded items, packs and foci never
/// move; a full pack is only used when the item joins a stack there; an item that failed to move
/// is left alone until its block runs out.
///
/// With <see cref="PlanOptions"/>, once nothing needs to change packs, every pack is tidied in
/// place: two stacks of one kind are joined, then items are put in order (<see cref="PackOrder"/>).
/// The main pack only once every item in it has settled.
///
/// It converges: a route takes an item to its one place and nothing sends it back (its place
/// depends on the rules and the packs carried, not on where it is); a join leaves one stack fewer
/// that isn't full; a reorder grows the run of items already in order. Each kind of move is
/// finite, so a fixed inventory runs out of moves. As a backstop, an item put in order
/// <see cref="MaxReorders"/> times within <see cref="ReorderWindowMs"/> isn't reordered again
/// until that window passes (an order the engine and the server see differently can't loop).
/// Pure (no host calls): the tests drive it with hand-made inventories.
/// </summary>
internal sealed class SortPlanner
{
    public const int SideCapacityFallback = 24;
    public const int MainCapacityFallback = 102;
    public const int MaxReorders = 6;
    public const long ReorderWindowMs = 10 * 60_000;

    private readonly Dictionary<uint, long> _firstSeen = new();
    private readonly Dictionary<uint, long> _blocked = new();
    private readonly Dictionary<uint, (int Count, long Since)> _reorders = new();

    public SortView View { get; private set; } = new();

    /// <summary>
    /// Items the player touched lately (<see cref="TouchWatch"/>): they don't move, stacks don't
    /// join into them and they aren't put in order (other items still are, around them). Unpack
    /// and Put back ignore it (the player asked to move everything). Null = nothing held.
    /// </summary>
    public Func<uint, bool>? Held;

    private bool IsHeld(SortItem it) => Held != null && Held(it.Id);

    /// <summary>Forget everything (logout, another character).</summary>
    public void Reset()
    {
        _firstSeen.Clear();
        _blocked.Clear();
        _reorders.Clear();
        View = new SortView();
    }

    /// <summary>Leave <paramref name="itemId"/> alone until <paramref name="untilMs"/> (a move failed).</summary>
    public void Block(uint itemId, long untilMs) => _blocked[itemId] = untilMs;

    public bool IsBlocked(uint itemId, long nowMs) => _blocked.TryGetValue(itemId, out long until) && until > nowMs;

    public int BlockedCount(long nowMs)
    {
        int n = 0;
        foreach (long until in _blocked.Values) if (until > nowMs) n++;
        return n;
    }

    /// <summary>An item was put in order (a reorder move landed).</summary>
    public void NoteReorder(uint itemId, long nowMs)
    {
        if (_reorders.TryGetValue(itemId, out var r) && nowMs - r.Since < ReorderWindowMs) _reorders[itemId] = (r.Count + 1, r.Since);
        else _reorders[itemId] = (1, nowMs);
    }

    /// <summary>The item has been put in order <see cref="MaxReorders"/> times lately: not again for now.</summary>
    public bool ReorderLimited(uint itemId, long nowMs) =>
        _reorders.TryGetValue(itemId, out var r) && nowMs - r.Since < ReorderWindowMs && r.Count >= MaxReorders;

    /// <summary>Worn and wielded items, packs and foci never move.</summary>
    public static bool Sortable(SortItem it) =>
        !it.Wielded && it.ObjectClass != ObjectClasses.Container && it.ObjectClass != ObjectClasses.Foci;

    public static int CapacityOf(SortPack p, int fallback) => p.Capacity > 0 ? p.Capacity : fallback;

    /// <summary>
    /// A stack in <paramref name="pack"/> that <paramref name="it"/> can join (the same kind, with room
    /// left), or 0. That is what lets a full pack still take the item.
    /// </summary>
    public static uint StackTarget(SortItem it, SortPack pack, Func<uint, bool>? skip = null)
    {
        if (it.MaxStack <= 1) return 0;
        foreach (SortItem other in pack.Items)
        {
            if (other.Id == it.Id || !SameKind(it, other) || (skip != null && skip(other.Id))) continue;
            int max = other.MaxStack > 1 ? other.MaxStack : it.MaxStack;
            if (other.Stack < max) return other.Id;
        }
        return 0;
    }

    internal static bool SameKind(SortItem a, SortItem b) =>
        a.Wcid != 0 && b.Wcid != 0 ? a.Wcid == b.Wcid : string.Equals(a.Name, b.Name, StringComparison.Ordinal) && a.Name.Length > 0;

    private static bool HasRoom(SortItem it) => it.MaxStack > 1 && it.Stack < it.MaxStack;

    /// <summary>
    /// Two stacks of one kind in <paramref name="pack"/> that both have room: the smaller one
    /// (<paramref name="from"/>) joins the larger (<paramref name="into"/>). The first such kind in
    /// the pack's order; the same answer for the same pack. False when every kind has at most one
    /// stack with room.
    /// </summary>
    public static bool StackJoin(SortPack pack, Func<SortItem, bool> movable, out SortItem from, out SortItem into, Func<SortItem, bool>? joinable = null)
    {
        from = into = null!;
        List<SortItem> items = pack.Items;
        var seen = new bool[items.Count];
        // A stack that may take part at all (joinable: not one the player is holding).
        bool Part(SortItem it) => HasRoom(it) && Sortable(it) && (joinable == null || joinable(it));
        for (int i = 0; i < items.Count; i++)
        {
            if (seen[i] || !Part(items[i])) continue;
            SortItem? small = null, big = null;
            for (int j = i; j < items.Count; j++)
            {
                SortItem it = items[j];
                if (j > i && (seen[j] || !Part(it) || !SameKind(items[i], it))) continue;
                seen[j] = true;
                if (big == null || it.Stack > big.Stack || (it.Stack == big.Stack && it.Id < big.Id)) big = it;
            }
            foreach (SortItem it in items)
            {
                if (ReferenceEquals(it, big) || !Part(it) || !SameKind(items[i], it) || !movable(it)) continue;
                if (small == null || it.Stack < small.Stack || (it.Stack == small.Stack && it.Id > small.Id)) small = it;
            }
            if (small != null && big != null) { from = small; into = big; return true; }
        }
        return false;
    }

    /// <summary>The moves to make now (routes only, as before ordering existed). See the overload.</summary>
    public List<SortMove> Plan(SortInventory inv, IReadOnlyList<SortRule> rules, SortScope scope, bool force, long nowMs, int settleMs) =>
        Plan(inv, rules, scope, force, nowMs, settleMs, default);

    /// <summary>
    /// The moves to make now for <paramref name="scope"/> (not <see cref="SortScope.Unpack"/>: see
    /// <see cref="PlanUnpack"/>), first one first: items to their packs, then (All and Clean, with
    /// <paramref name="opt"/>, when no item needs another pack) stacks joined, then items put in
    /// order, a pack at a time. Also refreshes <see cref="View"/> and the main pack's settle clock
    /// (an item's time starts when a pass first sees it in the main pack).
    /// </summary>
    public List<SortMove> Plan(SortInventory inv, IReadOnlyList<SortRule> rules, SortScope scope, bool force, long nowMs, int settleMs, PlanOptions opt)
    {
        bool everyPack = scope != SortScope.Main;
        bool evict = scope == SortScope.Clean;
        bool inPack = scope is SortScope.All or SortScope.Clean;
        var moves = new List<SortMove>();
        var view = new SortView();
        var used = new int[inv.Packs.Count + 1];
        for (int i = 0; i < inv.Packs.Count; i++) used[i + 1] = inv.Packs[i].Items.Count;
        int mainUsed = inv.Main.Items.Count;
        int mainCap = CapacityOf(inv.Main, MainCapacityFallback);
        bool kindFirst = opt.KindFirst;
        Func<SortItem, bool>? handy = opt.Place ? opt.Handy : null;
        Comparison<SortItem>? mainOrder = handy != null ? PackOrder.HandyFirst(handy, kindFirst) : null;
        bool MovableNow(SortItem it) => Sortable(it) && !IsBlocked(it.Id, nowMs) && !IsHeld(it);

        int PlaceIn(SortPack dest, SortItem wo, uint joins)
        {
            if (!opt.Place || joins != 0 || !dest.OrderKnown) return -1;
            if (handy != null && ReferenceEquals(dest, inv.Main))
            {
                // Into the main pack with handy items kept in front: in order (handy first), or a
                // handy item at the end of the handy run and anything else at the end.
                if (opt.Order) return PackOrder.InsertAt(dest.Items, wo, mainOrder!);
                return handy(wo) ? PackOrder.FrontRun(dest.Items, handy, MovableNow) : -1;
            }
            return PackOrder.InsertAt(dest.Items, wo, kindFirst);
        }

        uint PackIdOf(int index) => index == 0 ? inv.Main.Id : inv.Packs[index - 1].Id;

        // The next pack with the same rule as the item's (another rule that is on and matches it, in
        // the order RuleFor ranks them) when the first one's pack (first) is full: one with room, or a
        // stack the item can join; the pack it is in already when that is one of them (it stays). 0 = none.
        int Overflow(SortItem wo, int first, int from, out uint into)
        {
            into = 0;
            foreach (SortRule other in SortRules.RulesFor(rules, wo))
            {
                int n = SortRules.Resolve(other, inv, out _);
                if (n == 0 || n == first) continue;
                if (n == from) return n;
                SortPack p = inv.Packs[n - 1];
                uint join = StackTarget(wo, p, Held);
                if (used[n] < CapacityOf(p, SideCapacityFallback) || join != 0) { into = join; return n; }
            }
            return 0;
        }

        void Consider(SortItem wo, int from, bool settled, long settleLeftMs)
        {
            if (!Sortable(wo) || IsBlocked(wo.Id, nowMs)) return;
            bool held = IsHeld(wo);
            SortRule? r = SortRules.RuleFor(rules, wo);
            JobPlan? jobs = opt.Jobs;
            if (jobs != null && (r == null || !SortRules.MatchesName(r, wo)))
            {
                // A name rule first (the player named it), then a pack whose job picks it, then a kind rule.
                if (jobs.Frozen(from)) return;   // its pack's job can't be told now (RynthAi): it stays
                int firstJob = 0, jobTo = 0;
                uint jobInto = 0;
                foreach (int n in jobs.PacksFor(wo))
                {
                    if (n == from) return;       // in a pack whose job takes it already
                    if (firstJob == 0) firstJob = n;
                    if (jobTo != 0) continue;
                    SortPack jp = inv.Packs[n - 1];
                    uint join = StackTarget(wo, jp, Held);
                    if (used[n] < CapacityOf(jp, SideCapacityFallback) || join != 0) { jobTo = n; jobInto = join; }
                }
                if (firstJob != 0)
                {
                    if (held) { view.Held.Add(new HeldLine(wo, from, firstJob)); return; }
                    if (jobTo == 0)
                    {
                        view.Full.Add(firstJob);
                        view.Waiting++;
                        view.WaitFull++;
                        view.NoFit.Add(new NoFitLine(wo, firstJob, from));
                        return;
                    }
                    if (jobTo != firstJob) view.Full.Add(firstJob);
                    Go(wo, from, jobTo, jobInto, settled, settleLeftMs);
                    return;
                }
            }
            if (r == null && evict && from > 0)
            {
                // In a side pack and no rule wants it: back to the main pack, while there is room.
                if (held)
                {
                    view.Held.Add(new HeldLine(wo, from, 0));
                    return;
                }
                uint joins = StackTarget(wo, inv.Main, Held);
                if (mainUsed < mainCap || joins != 0)
                {
                    moves.Add(new SortMove(wo, inv.Main, 0, joins, PlaceIn(inv.Main, wo, joins), MoveKind.Route, PackIdOf(from), from));
                    if (joins == 0) mainUsed++;
                }
                else
                {
                    view.MainFull = true;
                    view.Waiting++;
                    view.WaitMainFull++;
                }
                return;
            }
            if (r == null) return;
            int to = SortRules.Resolve(r, inv, out _);
            if (to == 0)
            {
                view.Waiting++;   // reported once per rule below (Issues / Missing)
                view.WaitNoPack++;
                return;
            }
            if (to == from) return;
            if (held)
            {
                view.Held.Add(new HeldLine(wo, from, to));
                return;
            }
            SortPack dest = inv.Packs[to - 1];
            uint into = StackTarget(wo, dest, Held);
            if (used[to] >= CapacityOf(dest, SideCapacityFallback) && into == 0)
            {
                // Its pack is full: the next pack with the same rule (an overflow pack) that has room,
                // else it waits where it is and the window and chat say what didn't fit.
                view.Full.Add(to);
                int first = to;
                to = Overflow(wo, first, from, out into);
                if (to == from && to != 0) return;   // in an overflow pack already: it stays there
                if (to == 0)
                {
                    view.Waiting++;
                    view.WaitFull++;
                    view.NoFit.Add(new NoFitLine(wo, first, from));
                    return;
                }
            }
            Go(wo, from, to, into, settled, settleLeftMs);
        }

        // The item goes to side pack `to` (joining stack `into`), once it has settled.
        void Go(SortItem wo, int from, int to, uint into, bool settled, long settleLeftMs)
        {
            if (!settled)
            {
                view.Waiting++;
                view.WaitSettle++;
                view.Settling.Add(wo.Id);
                if (view.NextSettleMs < 0 || settleLeftMs < view.NextSettleMs) view.NextSettleMs = Math.Max(0, settleLeftMs);
                return;
            }
            SortPack dest = inv.Packs[to - 1];
            moves.Add(new SortMove(wo, dest, to, into, PlaceIn(dest, wo, into), MoveKind.Route, PackIdOf(from), from));
            if (into == 0) used[to]++;
        }

        var inMain = new HashSet<uint>();
        bool mainSettled = true;
        foreach (SortItem wo in inv.Main.Items)
        {
            inMain.Add(wo.Id);
            if (!_firstSeen.TryGetValue(wo.Id, out long seen)) _firstSeen[wo.Id] = seen = nowMs;
            bool settled = force || nowMs - seen >= settleMs;
            if (!settled) mainSettled = false;
            Consider(wo, 0, settled, settleMs - (nowMs - seen));
        }
        if (_firstSeen.Count > inMain.Count)
        {
            var gone = new List<uint>();
            foreach (uint id in _firstSeen.Keys) if (!inMain.Contains(id)) gone.Add(id);
            foreach (uint id in gone) _firstSeen.Remove(id);
        }
        if (everyPack)
        {
            for (int i = 0; i < inv.Packs.Count; i++)
                foreach (SortItem wo in inv.Packs[i].Items) Consider(wo, i + 1, true, 0);
        }

        bool ordering = inPack && opt.Order;
        if (inPack && moves.Count == 0)
        {
            // Nothing changes packs: tidy each pack in place. Side packs first, then the main pack
            // once nothing in it is new (loot still settling there would be ordered twice).
            bool Movable(SortItem it) => Sortable(it) && !IsBlocked(it.Id, nowMs) && !IsHeld(it);
            bool Joinable(SortItem it) => !IsHeld(it);
            var stacks = new List<SortMove>();
            var reorders = new List<SortMove>();
            for (int i = 0; i <= inv.Packs.Count; i++)
            {
                bool main = i == inv.Packs.Count;
                if (main && !mainSettled) break;
                SortPack pack = main ? inv.Main : inv.Packs[i];
                int index = main ? 0 : i + 1;
                if (opt.Merge && StackJoin(pack, Movable, out SortItem from, out SortItem into, Joinable))
                {
                    stacks.Add(new SortMove(from, pack, index, into.Id, -1, MoveKind.Stack, pack.Id, index));
                    continue;
                }
                bool Reorderable(SortItem it) => Movable(it) && !ReorderLimited(it.Id, nowMs);
                if (main && handy != null && pack.OrderKnown)
                {
                    // Handy items at the front: in order with them first, or (ordering off) only them moved.
                    if (ordering ? PackOrder.AllNamed(pack.Items) && PackOrder.NextMove(pack.Items, mainOrder!, Reorderable, out SortItem hi, out int hat)
                                 : PackOrder.FrontMove(pack.Items, handy, Reorderable, out hi, out hat))
                        reorders.Add(new SortMove(hi, pack, index, 0, hat, MoveKind.Reorder, pack.Id, index));
                    continue;
                }
                if (ordering && pack.OrderKnown && PackOrder.AllNamed(pack.Items)
                    && PackOrder.NextMove(pack.Items, kindFirst, Reorderable, out SortItem item, out int at))
                    reorders.Add(new SortMove(item, pack, index, 0, at, MoveKind.Reorder, pack.Id, index));
            }
            moves.AddRange(stacks);
            moves.AddRange(reorders);
        }

        FillPacks(view, inv, ordering, kindFirst, handy, MovableNow);
        FillIssues(view, inv, rules);
        View = view;
        return moves;
    }

    /// <summary>
    /// Emptying side packs into the main pack: every sortable item in side pack <paramref name="which"/>
    /// (1-based; null = all of them), while the main pack has room or the item joins a stack there.
    /// </summary>
    public List<SortMove> PlanUnpack(SortInventory inv, int? which, long nowMs)
    {
        var moves = new List<SortMove>();
        int used = inv.Main.Items.Count;
        int cap = CapacityOf(inv.Main, MainCapacityFallback);
        int waiting = 0;
        for (int i = 0; i < inv.Packs.Count; i++)
        {
            if (which != null && which.Value != i + 1) continue;
            foreach (SortItem wo in inv.Packs[i].Items)
            {
                if (!Sortable(wo) || IsBlocked(wo.Id, nowMs)) continue;
                uint joins = StackTarget(wo, inv.Main);
                if (used < cap || joins != 0)
                {
                    moves.Add(new SortMove(wo, inv.Main, 0, joins, -1, MoveKind.Route, inv.Packs[i].Id, i + 1));
                    if (joins == 0) used++;
                }
                else waiting++;
            }
        }
        var view = new SortView { Waiting = waiting, WaitMainFull = waiting, MainFull = waiting > 0 };
        FillPacks(view, inv, false, false);
        View = view;
        return moves;
    }

    /// <summary>
    /// Putting back what the last sort moved (<paramref name="made"/>, oldest first): newest first,
    /// each item still in the pack it was sent to goes back to the pack it came from, while that pack
    /// is carried and has room. An item gone (sold, joined into a stack) or moved since is skipped.
    /// Never joins stacks (it was a stack of its own), never holds (the player asked).
    /// </summary>
    public List<SortMove> PlanPutBack(SortInventory inv, IReadOnlyList<MadeMove> made, long nowMs)
    {
        var moves = new List<SortMove>();
        var used = new int[inv.Packs.Count + 1];
        used[0] = inv.Main.Items.Count;
        for (int i = 0; i < inv.Packs.Count; i++) used[i + 1] = inv.Packs[i].Items.Count;
        var where = new Dictionary<uint, (SortItem Item, int Index)>();
        foreach (SortItem it in inv.Main.Items) where[it.Id] = (it, 0);
        for (int i = 0; i < inv.Packs.Count; i++)
            foreach (SortItem it in inv.Packs[i].Items) where[it.Id] = (it, i + 1);

        var view = new SortView();
        var taken = new HashSet<uint>();
        for (int k = made.Count - 1; k >= 0; k--)
        {
            MadeMove m = made[k];
            if (!taken.Add(m.ItemId)) continue;   // only its latest move counts
            if (!where.TryGetValue(m.ItemId, out var at) || !Sortable(at.Item) || IsBlocked(m.ItemId, nowMs)) continue;
            SortPack here = at.Index == 0 ? inv.Main : inv.Packs[at.Index - 1];
            if (here.Id != m.ToId) continue;      // moved since: not ours to undo
            int to = m.FromId == inv.Main.Id ? 0 : inv.IndexOf(m.FromId);
            if (to == 0 && m.FromId != inv.Main.Id)
            {
                view.Waiting++;
                view.WaitNoPack++;
                continue;
            }
            SortPack dest = to == 0 ? inv.Main : inv.Packs[to - 1];
            if (used[to] >= CapacityOf(dest, to == 0 ? MainCapacityFallback : SideCapacityFallback))
            {
                if (to == 0) { view.MainFull = true; view.WaitMainFull++; }
                else { view.Full.Add(to); view.WaitFull++; }
                view.Waiting++;
                continue;
            }
            moves.Add(new SortMove(at.Item, dest, to, 0, -1, MoveKind.Route, here.Id, at.Index));
            used[to]++;
            used[at.Index]--;
        }
        FillPacks(view, inv, false, false);
        View = view;
        return moves;
    }

    private static PackLine LineOf(SortPack p, int capacityFallback, bool ordering, bool kindFirst) => new()
    {
        Id = p.Id,
        Name = p.Name.Length > 0 ? p.Name : "Pack",
        Used = p.Items.Count,
        Capacity = CapacityOf(p, capacityFallback),
        Summary = p.Summary(),
        OrderKnown = p.OrderKnown,
        OutOfPlace = ordering && p.OrderKnown ? PackOrder.OutOfPlace(p.Items, kindFirst) : 0,
        ColumnSlot = p.ColumnSlot,
    };

    private static void FillPacks(SortView view, SortInventory inv, bool ordering, bool kindFirst,
                                  Func<SortItem, bool>? handy = null, Func<SortItem, bool>? movable = null)
    {
        view.MainUsed = inv.Main.Items.Count;
        view.MainCapacity = CapacityOf(inv.Main, MainCapacityFallback);
        view.Main = LineOf(inv.Main, MainCapacityFallback, ordering, kindFirst);
        view.Main.Name = "Main pack";
        view.Handy = handy != null && inv.Main.OrderKnown;
        if (view.Handy)
        {
            // What is left to do in the main pack: out of the order with handy items first, or the handy items not in front yet.
            Func<SortItem, bool> canMove = movable ?? Sortable;
            view.HandyBehind = PackOrder.HandyBehind(inv.Main.Items, handy!, canMove);
            view.Main.OutOfPlace = ordering ? PackOrder.OutOfPlace(inv.Main.Items, PackOrder.HandyFirst(handy!, kindFirst)) : view.HandyBehind;
        }
        view.PackOrderKnown = inv.PackOrderKnown;
        view.OrderSource = inv.OrderSource;
        view.Ordering = ordering;
        foreach (SortPack p in inv.Packs) view.Packs.Add(LineOf(p, SideCapacityFallback, ordering, kindFirst));
    }

    /// <summary>Every rule that is on and whose pack isn't carried (whether or not an item wants it now).</summary>
    private static void FillIssues(SortView view, SortInventory inv, IReadOnlyList<SortRule> rules)
    {
        for (int i = 0; i < rules.Count; i++)
        {
            SortRule r = rules[i];
            if (!r.On) continue;
            int to = SortRules.Resolve(r, inv, out bool fellBack);
            if (to != 0 && !fellBack) continue;
            if (to == 0) view.Missing.Add(r.Pack);
            view.Issues.Add(new RulePackIssue { Rule = r, RuleNumber = i + 1, UsedPack = to });
        }
    }
}

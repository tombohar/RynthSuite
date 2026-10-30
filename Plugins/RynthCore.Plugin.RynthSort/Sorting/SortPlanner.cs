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
}

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

    public SortMove(SortItem item, SortPack dest, int destIndex, uint mergeInto, int placement = -1, MoveKind kind = MoveKind.Route)
    {
        Item = item;
        Dest = dest;
        DestIndex = destIndex;
        MergeInto = mergeInto;
        Placement = placement;
        Kind = kind;
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
}

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
    public static uint StackTarget(SortItem it, SortPack pack)
    {
        if (it.MaxStack <= 1) return 0;
        foreach (SortItem other in pack.Items)
        {
            if (other.Id == it.Id || !SameKind(it, other)) continue;
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
    public static bool StackJoin(SortPack pack, Func<SortItem, bool> movable, out SortItem from, out SortItem into)
    {
        from = into = null!;
        List<SortItem> items = pack.Items;
        var seen = new bool[items.Count];
        for (int i = 0; i < items.Count; i++)
        {
            if (seen[i] || !HasRoom(items[i]) || !Sortable(items[i])) continue;
            SortItem? small = null, big = null;
            for (int j = i; j < items.Count; j++)
            {
                SortItem it = items[j];
                if (j > i && (seen[j] || !HasRoom(it) || !Sortable(it) || !SameKind(items[i], it))) continue;
                seen[j] = true;
                if (big == null || it.Stack > big.Stack || (it.Stack == big.Stack && it.Id < big.Id)) big = it;
            }
            foreach (SortItem it in items)
            {
                if (ReferenceEquals(it, big) || !HasRoom(it) || !Sortable(it) || !SameKind(items[i], it) || !movable(it)) continue;
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

        int PlaceIn(SortPack dest, SortItem wo, uint joins) =>
            opt.Place && joins == 0 && dest.OrderKnown ? PackOrder.InsertAt(dest.Items, wo, kindFirst) : -1;

        void Consider(SortItem wo, int from, bool settled)
        {
            if (!Sortable(wo) || IsBlocked(wo.Id, nowMs)) return;
            SortRule? r = SortRules.RuleFor(rules, wo);
            if (r == null && evict && from > 0)
            {
                // In a side pack and no rule wants it: back to the main pack, while there is room.
                uint joins = StackTarget(wo, inv.Main);
                if (mainUsed < mainCap || joins != 0)
                {
                    moves.Add(new SortMove(wo, inv.Main, 0, joins, PlaceIn(inv.Main, wo, joins)));
                    if (joins == 0) mainUsed++;
                }
                else
                {
                    view.MainFull = true;
                    view.Waiting++;
                }
                return;
            }
            if (r == null) return;
            int to = SortRules.Resolve(r, inv, out _);
            if (to == 0)
            {
                view.Waiting++;   // reported once per rule below (Issues / Missing)
                return;
            }
            if (to == from) return;
            SortPack dest = inv.Packs[to - 1];
            uint into = StackTarget(wo, dest);
            if (used[to] >= CapacityOf(dest, SideCapacityFallback) && into == 0)
            {
                view.Full.Add(to);
                view.Waiting++;
                return;
            }
            if (!settled)
            {
                view.Waiting++;
                return;
            }
            moves.Add(new SortMove(wo, dest, to, into, PlaceIn(dest, wo, into)));
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
            Consider(wo, 0, settled);
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
                foreach (SortItem wo in inv.Packs[i].Items) Consider(wo, i + 1, true);
        }

        bool ordering = inPack && opt.Order;
        if (inPack && moves.Count == 0)
        {
            // Nothing changes packs: tidy each pack in place. Side packs first, then the main pack
            // once nothing in it is new (loot still settling there would be ordered twice).
            bool Movable(SortItem it) => Sortable(it) && !IsBlocked(it.Id, nowMs);
            var stacks = new List<SortMove>();
            var reorders = new List<SortMove>();
            for (int i = 0; i <= inv.Packs.Count; i++)
            {
                bool main = i == inv.Packs.Count;
                if (main && !mainSettled) break;
                SortPack pack = main ? inv.Main : inv.Packs[i];
                int index = main ? 0 : i + 1;
                if (opt.Merge && StackJoin(pack, Movable, out SortItem from, out SortItem into))
                {
                    stacks.Add(new SortMove(from, pack, index, into.Id, -1, MoveKind.Stack));
                    continue;
                }
                if (ordering && pack.OrderKnown && PackOrder.AllNamed(pack.Items)
                    && PackOrder.NextMove(pack.Items, kindFirst, it => Movable(it) && !ReorderLimited(it.Id, nowMs), out SortItem item, out int at))
                    reorders.Add(new SortMove(item, pack, index, 0, at, MoveKind.Reorder));
            }
            moves.AddRange(stacks);
            moves.AddRange(reorders);
        }

        FillPacks(view, inv, ordering, kindFirst);
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
                    moves.Add(new SortMove(wo, inv.Main, 0, joins));
                    if (joins == 0) used++;
                }
                else waiting++;
            }
        }
        var view = new SortView { Waiting = waiting, MainFull = waiting > 0 };
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
    };

    private static void FillPacks(SortView view, SortInventory inv, bool ordering, bool kindFirst)
    {
        view.MainUsed = inv.Main.Items.Count;
        view.MainCapacity = CapacityOf(inv.Main, MainCapacityFallback);
        view.Main = LineOf(inv.Main, MainCapacityFallback, ordering, kindFirst);
        view.Main.Name = "Main pack";
        view.PackOrderKnown = inv.PackOrderKnown;
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

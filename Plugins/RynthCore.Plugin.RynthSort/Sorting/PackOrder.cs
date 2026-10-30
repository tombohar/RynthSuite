using System;
using System.Collections.Generic;

namespace RynthCore.Plugin.RynthSort.Sorting;

/// <summary>
/// The order items keep inside a pack, and the moves that get a pack there. Pure: the tests drive it.
///
/// The order: by name A-Z (any case, then exact), so like items sit together; the same name with
/// another kind of stack (class id) next; then by object id, which never changes. Optionally by
/// kind first (the class's label: "Armor", "Gem", ...), then the same. It is a total order on the
/// items, so a pack has exactly one target order however its items got there.
///
/// The moves: one item at a time to a slot (the engine's PutItemInContainer, API v80: placement =
/// the index it should end at, counted without it, which is how ACE inserts). The items already in
/// order relative to each other (a longest increasing run of target ranks) stay; one of the others
/// is put right after the last of those that comes before it. That run grows by at least one with
/// every move, so a pack of n items is in order after at most n - (longest run) moves, and an
/// item that arrived in the right place costs none.
/// </summary>
internal static class PackOrder
{
    /// <summary>The target order (see the class summary). <paramref name="kindFirst"/>: by class label first.</summary>
    public static int Compare(SortItem a, SortItem b, bool kindFirst)
    {
        if (ReferenceEquals(a, b)) return 0;
        if (kindFirst && a.ObjectClass != b.ObjectClass)
        {
            int k = string.Compare(ObjectClasses.Label(a.ObjectClass), ObjectClasses.Label(b.ObjectClass), StringComparison.OrdinalIgnoreCase);
            if (k != 0) return k;
            k = a.ObjectClass.CompareTo(b.ObjectClass);
            if (k != 0) return k;
        }
        string an = a.Name ?? string.Empty, bn = b.Name ?? string.Empty;
        int c = string.Compare(an, bn, StringComparison.OrdinalIgnoreCase);
        if (c != 0) return c;
        c = string.CompareOrdinal(an, bn);
        if (c != 0) return c;
        c = a.Wcid.CompareTo(b.Wcid);
        return c != 0 ? c : a.Id.CompareTo(b.Id);
    }

    /// <summary>Every item has a name: an item the client hasn't described yet would sort as "" and move again once named.</summary>
    public static bool AllNamed(IReadOnlyList<SortItem> items)
    {
        foreach (SortItem it in items)
            if (string.IsNullOrEmpty(it.Name)) return false;
        return true;
    }

    /// <summary>Each item's place in the target order (0 = first), in the items' current order.</summary>
    public static int[] Ranks(IReadOnlyList<SortItem> items, bool kindFirst)
    {
        int n = items.Count;
        var idx = new int[n];
        for (int i = 0; i < n; i++) idx[i] = i;
        Array.Sort(idx, (x, y) => Compare(items[x], items[y], kindFirst));
        var rank = new int[n];
        for (int r = 0; r < n; r++) rank[idx[r]] = r;
        return rank;
    }

    /// <summary>
    /// Which positions are in a longest increasing run of <paramref name="rank"/> (they stay put).
    /// O(n^2) over a pack (at most ~100 items); the earliest-ending run is kept, so the answer is
    /// the same for the same list.
    /// </summary>
    public static bool[] Kept(int[] rank)
    {
        int n = rank.Length;
        var len = new int[n];
        var prev = new int[n];
        int best = -1;
        for (int i = 0; i < n; i++)
        {
            len[i] = 1;
            prev[i] = -1;
            for (int j = 0; j < i; j++)
                if (rank[j] < rank[i] && len[j] + 1 > len[i]) { len[i] = len[j] + 1; prev[i] = j; }
            if (best < 0 || len[i] > len[best]) best = i;
        }
        var kept = new bool[n];
        for (int i = best; i >= 0; i = prev[i]) kept[i] = true;
        return kept;
    }

    /// <summary>How many items are out of place (would move): 0 = in order.</summary>
    public static int OutOfPlace(IReadOnlyList<SortItem> items, bool kindFirst)
    {
        if (items.Count < 2) return 0;
        bool[] kept = Kept(Ranks(items, kindFirst));
        int n = 0;
        foreach (bool k in kept) if (!k) n++;
        return n;
    }

    /// <summary>
    /// The next move that puts the pack in order: the out-of-place item with the lowest target rank
    /// that <paramref name="movable"/> allows, and the slot it goes to (counted without it). False
    /// when the pack is in order, or only items that can't move now are out of place.
    /// </summary>
    public static bool NextMove(IReadOnlyList<SortItem> items, bool kindFirst, Func<SortItem, bool> movable, out SortItem item, out int placement)
    {
        item = null!;
        placement = -1;
        int n = items.Count;
        if (n < 2) return false;
        int[] rank = Ranks(items, kindFirst);
        bool[] kept = Kept(rank);
        int pick = -1;
        for (int i = 0; i < n; i++)
            if (!kept[i] && movable(items[i]) && (pick < 0 || rank[i] < rank[pick])) pick = i;
        if (pick < 0) return false;

        // Right after the last kept item that comes before it in the target order.
        int after = -1;
        for (int i = 0; i < n; i++)
            if (kept[i] && rank[i] < rank[pick]) after = i;   // kept items rise with i: the last one wins
        item = items[pick];
        placement = after < 0 ? 0 : (after > pick ? after : after + 1);
        return true;
    }

    /// <summary>
    /// The slot an item arriving in a pack should take so it lands where the order wants it: after
    /// every item there that comes before it. In a pack already in order that is its final place.
    /// </summary>
    public static int InsertAt(IReadOnlyList<SortItem> items, SortItem arriving, bool kindFirst)
    {
        int at = 0;
        for (int i = 0; i < items.Count; i++)
            if (Compare(items[i], arriving, kindFirst) < 0) at = i + 1;
        return at;
    }

    /// <summary>
    /// The list after moving <paramref name="item"/> to <paramref name="placement"/> as ACE does it
    /// (taken out, the others close up, put in at that index, clamped to the end). For the tests'
    /// pretend server and the planner's checks.
    /// </summary>
    public static void Apply(List<SortItem> items, SortItem item, int placement)
    {
        items.Remove(item);
        items.Insert(Math.Clamp(placement, 0, items.Count), item);
    }
}

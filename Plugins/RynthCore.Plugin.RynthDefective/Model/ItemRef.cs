using System;
using System.Collections.Generic;
using RynthCore.Plugin.RynthDefective.Items;

namespace RynthCore.Plugin.RynthDefective.Model;

/// <summary>
/// One item identity, as a slot, a favourite or a recent weapon keeps it: the object id it was
/// last seen with, and what the item IS (name, wcid, material) so another carried item of the same
/// identity can stand in when the object is gone (a used-up kit stack, a replaced weapon). The
/// icon ids are the last ones seen, to draw the item dimmed while none is carried.
/// </summary>
internal sealed class ItemRef
{
    public uint Id;
    public string Name = string.Empty;
    public uint Wcid;
    public int Material;
    public uint Icon, Underlay, Overlay;

    public static ItemRef From(CarriedItem it)
    {
        var r = new ItemRef();
        r.CopyFrom(it);
        return r;
    }

    /// <summary>Takes the item's identity and picture (the slot follows the item it is bound to).</summary>
    public void CopyFrom(CarriedItem it)
    {
        Id = it.Id;
        Name = it.Name;
        Wcid = it.Wcid;
        Material = it.Material;
        if (it.Icon != 0) { Icon = it.Icon; Underlay = it.Underlay; Overlay = it.Overlay; }
    }

    public ItemRef Clone() => (ItemRef)MemberwiseClone();

    public string FullName => ItemNames.Full(Name, Material);

    /// <summary>
    /// Whether <paramref name="it"/> is this item or another of the same kind: the same name (any
    /// case), and the same wcid and material wherever both sides know them. The id is not part of
    /// it - that is how a stack that was used up is replaced by the next one.
    /// </summary>
    public bool Matches(CarriedItem it)
    {
        if (!string.Equals(Name, it.Name, StringComparison.OrdinalIgnoreCase)) return false;
        if (Wcid != 0 && it.Wcid != 0 && Wcid != it.Wcid) return false;
        if (Material != 0 && it.Material != 0 && Material != it.Material) return false;
        return true;
    }

    /// <summary>Two references to the same thing (the same id, or the same identity).</summary>
    public bool SameAs(ItemRef other) =>
        (Id != 0 && Id == other.Id) || (string.Equals(Name, other.Name, StringComparison.OrdinalIgnoreCase)
                                        && Wcid == other.Wcid && Material == other.Material);
}

/// <summary>What a slot shows: the item it is bound to now, how many of it there are, and its state.</summary>
internal readonly record struct SlotStatus(CarriedItem? Item, int Count, int Uses, bool Rebound)
{
    public bool Missing => Item == null;
}

/// <summary>Finding the carried item for a reference, and the counts a slot shows.</summary>
internal static class ItemMatcher
{
    /// <summary>
    /// The carried item for <paramref name="r"/>: the very object when it is still carried (and still
    /// the same kind of item - an id can be reused), else another item of the same identity:
    /// not wielded before wielded, then the smaller stack (a used-up stack is gone sooner and the
    /// pack stays tidier), then the lower id. Null when nothing matches.
    /// </summary>
    public static CarriedItem? Find(ItemRef r, IReadOnlyList<CarriedItem> items, out bool rebound)
    {
        rebound = false;
        if (string.IsNullOrEmpty(r.Name)) return null;
        CarriedItem? best = null;
        foreach (CarriedItem it in items)
        {
            if (!r.Matches(it)) continue;
            if (it.Id == r.Id) return it;
            if (best == null || Better(it, best)) best = it;
        }
        rebound = best != null;
        return best;
    }

    private static bool Better(CarriedItem a, CarriedItem b)
    {
        if (a.Wielded != b.Wielded) return !a.Wielded;
        if (a.Stack != b.Stack) return a.Stack < b.Stack;
        return a.Id < b.Id;
    }

    /// <summary>The slot's item, how many of it are carried (stack sizes added up) and the uses left on them all.</summary>
    public static SlotStatus Status(ItemRef r, IReadOnlyList<CarriedItem> items)
    {
        CarriedItem? it = Find(r, items, out bool rebound);
        if (it == null) return new SlotStatus(null, 0, 0, false);
        int count = 0, uses = 0;
        foreach (CarriedItem other in items)
        {
            if (!r.Matches(other)) continue;
            count += Math.Max(1, other.Stack);
            if (other.MaxUses > 0) uses += Math.Max(0, other.Uses);
        }
        return new SlotStatus(it, count, uses, rebound);
    }

    /// <summary>
    /// Keeps a slot's reference up to date with what is carried: bound to the object found (a
    /// replacement after the old one is gone), with that object's name, picture and so on. A
    /// missing item changes nothing. Returns whether the reference changed (the caller saves).
    /// </summary>
    public static bool Maintain(ItemRef r, IReadOnlyList<CarriedItem> items)
    {
        CarriedItem? it = Find(r, items, out _);
        if (it == null) return false;
        uint id = r.Id, icon = r.Icon, under = r.Underlay, over = r.Overlay;
        int material = r.Material;
        uint wcid = r.Wcid;
        r.CopyFrom(it);
        return id != r.Id || icon != r.Icon || under != r.Underlay || over != r.Overlay || material != r.Material || wcid != r.Wcid;
    }

    /// <summary>
    /// Finds an item by what the player typed (/rdef use Peerless Healing Kit): the full name, the
    /// name, then names that start with the text, then ones that contain it (every word); the
    /// first by the same order as <see cref="Find"/>.
    /// </summary>
    public static CarriedItem? FindByName(string text, IReadOnlyList<CarriedItem> items)
    {
        string q = (text ?? string.Empty).Trim();
        if (q.Length == 0) return null;
        string[] words = q.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        CarriedItem? best = null;
        int bestRank = int.MaxValue;
        foreach (CarriedItem it in items)
        {
            int rank = NameRank(it, q, words);
            if (rank == int.MaxValue) continue;
            if (best == null || rank < bestRank || (rank == bestRank && Better(it, best)))
            {
                best = it;
                bestRank = rank;
            }
        }
        return best;
    }

    private static int NameRank(CarriedItem it, string q, string[] words)
    {
        string full = it.FullName;
        if (string.Equals(full, q, StringComparison.OrdinalIgnoreCase)) return 0;
        if (string.Equals(it.Name, q, StringComparison.OrdinalIgnoreCase)) return 1;
        if (full.StartsWith(q, StringComparison.OrdinalIgnoreCase) || it.Name.StartsWith(q, StringComparison.OrdinalIgnoreCase)) return 2;
        foreach (string w in words)
            if (full.IndexOf(w, StringComparison.OrdinalIgnoreCase) < 0) return int.MaxValue;
        return 3;
    }
}

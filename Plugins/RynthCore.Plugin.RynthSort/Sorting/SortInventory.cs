using System;
using System.Collections.Generic;
using System.Text;

namespace RynthCore.Plugin.RynthSort.Sorting;

/// <summary>One loose item in a pack, as read this pass (InventoryReader).</summary>
internal sealed class SortItem
{
    public uint Id;
    public string Name = string.Empty;
    /// <summary>ObjectClass number (<see cref="ObjectClasses"/>).</summary>
    public int ObjectClass;
    public uint Wcid;
    public int Stack = 1;
    public int MaxStack = 1;
    /// <summary>Worn or wielded (the reader leaves these out; kept so the planner can refuse them too).</summary>
    public bool Wielded { get; set; }
}

/// <summary>The main pack or one side pack and its loose items (packs and foci are never items).</summary>
internal sealed class SortPack
{
    public uint Id;
    public string Name = string.Empty;
    /// <summary>How many items it holds (ItemsCapacity; 102 for the main pack and 24 for a side pack when unknown).</summary>
    public int Capacity;
    public readonly List<SortItem> Items = new();
    /// <summary>
    /// <see cref="Items"/> are in the pack's real slot order (slot 0 first: the engine's
    /// GetContainerOrder, API v80, agreed with the contents read). False: object-id order, and
    /// nothing is reordered in this pack.
    /// </summary>
    public bool OrderKnown;

    /// <summary>
    /// Puts <see cref="Items"/> in <paramref name="order"/> (the pack's slot order) when that names
    /// exactly the items read, each once; else leaves them and <see cref="OrderKnown"/> false (a
    /// list a move behind the contents, or a pack the server hasn't listed: never guessed).
    /// </summary>
    public bool ApplyOrder(IReadOnlyList<uint> order, int count)
    {
        OrderKnown = false;
        if (count != Items.Count) return false;
        var byId = new Dictionary<uint, SortItem>(Items.Count);
        foreach (SortItem it in Items)
            if (!byId.TryAdd(it.Id, it)) return false;
        var sorted = new List<SortItem>(count);
        for (int i = 0; i < count; i++)
        {
            if (!byId.Remove(order[i], out SortItem? it)) return false;   // unknown here, or listed twice
            sorted.Add(it);
        }
        Items.Clear();
        Items.AddRange(sorted);
        OrderKnown = true;
        return true;
    }

    /// <summary>
    /// What is in it, in a few words: the 2-3 commonest kinds of item ("Salvage, Gems"), by
    /// class, or by name for the classes that say little (Misc, Unknown); "empty" when empty.
    /// </summary>
    public string Summary(int most = 3)
    {
        if (Items.Count == 0) return "empty";
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var order = new List<string>();
        foreach (SortItem it in Items)
        {
            string key = KindOf(it);
            if (key.Length == 0) continue;
            if (counts.TryGetValue(key, out int n)) counts[key] = n + 1;
            else { counts[key] = 1; order.Add(key); }
        }
        if (order.Count == 0) return "unknown items";
        // Commonest first; a tie keeps the order the kinds were met in.
        var ranked = new List<string>(order);
        ranked.Sort((a, b) => counts[a] != counts[b] ? counts[b].CompareTo(counts[a]) : order.IndexOf(a).CompareTo(order.IndexOf(b)));
        var sb = new StringBuilder();
        for (int i = 0; i < ranked.Count && i < most; i++)
        {
            if (i > 0) sb.Append(", ");
            sb.Append(ranked[i]);
        }
        return sb.ToString();
    }

    /// <summary>The kind an item counts as in <see cref="Summary"/>.</summary>
    internal static string KindOf(SortItem it)
    {
        int c = it.ObjectClass;
        if (c == ObjectClasses.Misc || c == ObjectClasses.Unknown || c == ObjectClasses.Bundle)
            return Shorten(it.Name);
        return ObjectClasses.Plural(c);
    }

    private static string Shorten(string? name)
    {
        string n = (name ?? string.Empty).Trim();
        return n.Length <= 24 ? n : n[..23] + "~";
    }
}

/// <summary>A side-slot thing in the main pack (a side pack or a focus), in the client's list order.</summary>
internal readonly struct SortSlot
{
    public readonly uint Id;
    /// <summary>ItemsCapacity: above 0 for a side pack, 0 for a focus, -1 when it couldn't be read.</summary>
    public readonly int Capacity;

    public SortSlot(uint id, int capacity)
    {
        Id = id;
        Capacity = capacity;
    }

    public bool IsPack => Capacity > 0;
}

/// <summary>
/// The character's packs: the main pack and the side packs, pack 1 first. With the engine's slot
/// order (API v80) that is the pack column on screen, top first (<see cref="PackOrderKnown"/>);
/// else GetContainerContents' order, by object id. Rules keep the pack's id either way, so a
/// pack keeps its rules whatever its number.
/// </summary>
internal sealed class SortInventory
{
    public SortPack Main = new();
    public readonly List<SortPack> Packs = new();
    /// <summary>
    /// Every container entry in the main pack, foci included, in object-id order (what the Lua
    /// script numbered its packs by: game.Character.Containers). Never reordered: the one-time
    /// fix of imported Lua pack numbers (<see cref="PackFix"/>) reads the Lua numbers through it.
    /// </summary>
    public readonly List<SortSlot> Slots = new();
    /// <summary><see cref="Packs"/> are in the pack column's order, top first.</summary>
    public bool PackOrderKnown;

    /// <summary>
    /// Puts <see cref="Packs"/> in the pack column's order: <paramref name="order"/> is the main
    /// pack's side-slot list (packs and foci, top first). Only when it names every pack read; else
    /// the packs keep their order and <see cref="PackOrderKnown"/> is false.
    /// </summary>
    public bool ApplyPackOrder(IReadOnlyList<uint> order, int count)
    {
        PackOrderKnown = false;
        var rank = new Dictionary<uint, int>(count);
        for (int i = 0; i < count; i++) rank.TryAdd(order[i], i);
        foreach (SortPack p in Packs)
            if (!rank.ContainsKey(p.Id)) return false;
        Packs.Sort((a, b) => rank[a.Id].CompareTo(rank[b.Id]));
        PackOrderKnown = true;
        return true;
    }

    /// <summary>The 1-based number of side pack <paramref name="id"/>, or 0 when it isn't carried.</summary>
    public int IndexOf(uint id)
    {
        if (id == 0) return 0;
        for (int i = 0; i < Packs.Count; i++)
            if (Packs[i].Id == id) return i + 1;
        return 0;
    }
}

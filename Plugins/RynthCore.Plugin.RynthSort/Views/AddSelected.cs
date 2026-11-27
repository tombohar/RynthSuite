using System;
using System.Collections.Generic;
using RynthCore.Plugin.RynthSort.Sorting;

namespace RynthCore.Plugin.RynthSort.Views;

/// <summary>What the game has selected, as far as a rule can use it.</summary>
internal enum SelectionKind
{
    /// <summary>This RynthCore can't say what is selected.</summary>
    Unavailable,
    /// <summary>The packs can't be read yet (not loaded).</summary>
    NoInventory,
    Nothing,
    /// <summary>A pack or focus (it holds items; it isn't one).</summary>
    Pack,
    /// <summary>Something else: on the ground, a monster, another player, a worn or wielded item.</summary>
    NotInPacks,
    /// <summary>A loose item in the main pack or a side pack.</summary>
    Item,
}

/// <summary>The selected object: its kind, and for an item the item as the packs were just read.</summary>
internal sealed class SelectedItem
{
    public SelectionKind Kind;
    public uint Id;
    /// <summary>The object's name as the client has it (blank when unknown); for an item, its name in the pack read.</summary>
    public string Name = string.Empty;
    public SortItem? Item;

    public static SelectedItem Of(SelectionKind kind, uint id = 0, string name = "") => new() { Kind = kind, Id = id, Name = name ?? string.Empty };
}

internal static class SelectionLookup
{
    /// <summary>
    /// What <paramref name="id"/> is in the packs just read: a loose item (the reader leaves out
    /// worn and wielded items, packs and foci), a pack or focus, or nothing the packs hold.
    /// </summary>
    public static SelectedItem Find(SortInventory inv, uint id, string name)
    {
        if (id == 0) return SelectedItem.Of(SelectionKind.Nothing);
        SortItem? item = inv.Main.Items.Find(x => x.Id == id);
        if (item == null)
            foreach (SortPack p in inv.Packs)
            {
                item = p.Items.Find(x => x.Id == id);
                if (item != null) break;
            }
        if (item != null) return new SelectedItem { Kind = SelectionKind.Item, Id = id, Name = item.Name, Item = item };
        foreach (SortSlot s in inv.Slots)
            if (s.Id == id) return SelectedItem.Of(SelectionKind.Pack, id, name);
        foreach (SortPack p in inv.Packs)
            if (p.Id == id) return SelectedItem.Of(SelectionKind.Pack, id, name);
        return SelectedItem.Of(SelectionKind.NotInPacks, id, name);
    }
}

internal enum AddStatus
{
    /// <summary>A name went into one of the pack's rules.</summary>
    Added,
    /// <summary>The pack's rules already take the item: nothing changed.</summary>
    AlreadyThere,
    /// <summary>Nothing selected, not an item in the packs, no such pack, no room: nothing changed.</summary>
    Refused,
}

internal sealed class AddOutcome
{
    public AddStatus Status;
    /// <summary>What to tell the player (the window's line under the pack's buttons; the chat for /rsort add).</summary>
    public string Message = string.Empty;
    /// <summary>The rule the name went into (null unless <see cref="Status"/> is Added).</summary>
    public SortRule? Rule;
}

/// <summary>
/// "Add selected": the item selected in the game goes into one of a pack's rules by name, in the
/// rules' own words ("name has ..."). The name is the item's name without a count: a rule's name
/// is part of an item's name, so the item's own name takes every item called that (a bag of
/// "Salvaged Iron (100)" becomes "Salvaged Iron", whatever the bag holds), and it needs no more
/// than the name the planner already matches on. A class would take every item of that kind,
/// and rules have no wcid or material to match by. Pure (no host calls): the plugin reads the
/// selection, the tests hand one in.
/// </summary>
internal static class AddSelectedRules
{
    /// <summary>The name an item's rule entry holds: the name without a trailing "(100)" count, as a rule keeps it.</summary>
    public static string EntryFor(string? itemName)
    {
        string n = (itemName ?? string.Empty).Trim();
        int open = n.LastIndexOf('(');
        if (open > 0 && n.EndsWith(')') && IsCount(n.AsSpan(open + 1, n.Length - open - 2)))
            n = n[..open].TrimEnd();
        return SortRule.Clean(n);
    }

    private static bool IsCount(ReadOnlySpan<char> s)
    {
        if (s.Length == 0) return false;
        foreach (char c in s) if (c < '0' || c > '9') return false;
        return true;
    }

    /// <summary>
    /// Puts the selected item into pack <paramref name="number"/>'s rules (1-based, as the window
    /// numbers them). Never moves anything: an item another pack's rule takes still gets its name
    /// in this pack's rules (the player pressed Add for this pack), and the message says which
    /// pack had it and whether this pack's rule now wins.
    /// </summary>
    public static AddOutcome Apply(List<SortRule> rules, SortView view, int number, SelectedItem sel)
    {
        AddOutcome Refuse(string text) => new() { Status = AddStatus.Refused, Message = text };

        string called = sel.Name.Length > 0 ? $"\"{sel.Name}\"" : "The selected object";
        switch (sel.Kind)
        {
            case SelectionKind.Unavailable: return Refuse("This RynthCore can't say which item is selected, Nothing was added.");
            case SelectionKind.NoInventory: return Refuse("Your packs aren't loaded yet; try again in a moment. Nothing was added.");
            case SelectionKind.Nothing: return Refuse("Nothing is selected. Click an item in your inventory, then press Add selected. Nothing was added.");
            case SelectionKind.Pack: return Refuse($"{called} is a pack, not an item. Select an item inside your packs. Nothing was added.");
            case SelectionKind.NotInPacks:
                return Refuse($"{called} isn't an item in your packs (on the ground, worn, or not yours). Select an item in your inventory. Nothing was added.");
        }
        if (sel.Item == null) return Refuse("Nothing is selected. Nothing was added.");
        if (number < 1 || number > view.Packs.Count)
            return Refuse(view.Packs.Count == 0 ? "You carry no side packs. Nothing was added." : $"You don't carry a pack {number} (you have {view.Packs.Count}). Nothing was added.");

        SortItem item = sel.Item;
        string entry = EntryFor(item.Name);
        if (entry.Length == 0) return Refuse("The selected item has no name the client can read yet; try again in a moment. Nothing was added.");

        string packName = PackWords(view, number);
        var here = new List<SortRule>();
        foreach (SortRule r in rules) if (SortPanel.ShownPack(view, r) == number) here.Add(r);
        here.Sort(SortRules.CompareForList);

        // Already named by one of this pack's rules (the entry itself, or a shorter name inside it)?
        SortRule? offRule = null;
        foreach (SortRule r in here)
        {
            if (!SortRules.MatchesName(r, item)) continue;
            if (r.On) return AlreadyThere(rules, view, number, item, $"\"{entry}\" is already in {packName}'s rules ({SortRules.Describe(r)}). Nothing was added.");
            offRule ??= r;
        }
        if (offRule != null)
            return AlreadyThere(rules, view, number, item,
                $"\"{entry}\" is already in {packName}'s rules ({SortRules.Describe(offRule)}), but that rule is turned off: turn it on. Nothing was added.");

        // Already sent here by a class rule of this pack (no other rule beats it)?
        SortRule? before = SortRules.RuleFor(rules, item);
        if (before != null && SortPanel.ShownPack(view, before) == number)
            return new AddOutcome { Status = AddStatus.AlreadyThere, Message = $"\"{entry}\" already goes to {packName} ({SortRules.Describe(before)}). Nothing was added." };

        // Where the name goes: a rule of this pack that is on and already lists names, else a new
        // rule (one row per item would run into the rule limit), else any rule of the pack with room.
        SortRule? target = null;
        foreach (SortRule r in here)
            if (r.On && r.Names.Count > 0 && r.Names.Count < SortRule.MaxNames) { target = r; break; }
        bool created = false;
        if (target == null && rules.Count < SortStore.MaxRules)
        {
            target = new SortRule();
            SortPanel.SetPack(target, view, number);
            created = true;
        }
        if (target == null)
            foreach (SortRule r in here)
                if (r.On && r.Names.Count < SortRule.MaxNames) { target = r; break; }
        if (target == null)
            return Refuse($"There is no room in {packName}'s rules (the limit of {SortStore.MaxRules} rules is reached and its rules hold {SortRule.MaxNames} names each). Nothing was added.");

        target.AddName(entry);
        if (created) rules.Add(target);

        string text = created
            ? $"Added \"{entry}\" to {packName} as a new rule."
            : $"Added \"{entry}\" to {packName}'s rule {SortRules.Describe(target)}.";
        if (before != null)
            text += $" It was going to {WhereWords(view, before)} ({SortRules.Describe(before)}).";
        SortRule? after = SortRules.RuleFor(rules, item);
        if (after != null && SortPanel.ShownPack(view, after) != number)
            text += $" The rule for {WhereWords(view, after)} ({SortRules.Describe(after)}) still wins, as it matches the same way and comes first: change that rule to send the item here.";
        else if (before != null) text += " Now it goes here.";
        return new AddOutcome { Status = AddStatus.Added, Message = text, Rule = target };
    }

    /// <summary>"Already there", and a word when another pack's rule is the one that takes the item.</summary>
    private static AddOutcome AlreadyThere(List<SortRule> rules, SortView view, int number, SortItem item, string text)
    {
        SortRule? winner = SortRules.RuleFor(rules, item);
        if (winner != null && SortPanel.ShownPack(view, winner) != number)
            text = text.Replace(" Nothing was added.", $" But the rule for {WhereWords(view, winner)} ({SortRules.Describe(winner)}) comes first and wins. Nothing was added.");
        return new AddOutcome { Status = AddStatus.AlreadyThere, Message = text };
    }

    /// <summary>"pack 2 (Sack)".</summary>
    private static string PackWords(SortView view, int number) => $"pack {number} ({view.Packs[number - 1].Name})";

    private static string WhereWords(SortView view, SortRule r)
    {
        int n = SortPanel.ShownPack(view, r);
        return n > 0 ? PackWords(view, n) : "a pack you don't have";
    }
}

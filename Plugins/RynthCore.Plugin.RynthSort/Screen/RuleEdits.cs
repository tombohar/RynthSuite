using System;
using System.Collections.Generic;
using RynthCore.Plugin.RynthSort.Sorting;
using RynthCore.Plugin.RynthSort.Views;

namespace RynthCore.Plugin.RynthSort.Screen;

/// <summary>What a rule change did: whether anything changed, and the toast that says so.</summary>
internal readonly record struct EditResult(bool Changed, string Text)
{
    public static EditResult Nothing(string text) => new(false, text);
}

/// <summary>
/// The window's rule changes, in the design's terms: a pack card's chips are the kinds and names its
/// rules hold. A kind sent to a pack leaves every other pack ("All gems"); a name sent to a pack
/// leaves every other pack's rules too and, being a name, beats any kind ("Only \"Black Opal\"");
/// a chip's × takes that one entry out of its rule (an emptied rule goes). Nothing here moves an
/// item. Pure: works on the rule list and the planner's view (the packs as numbered now).
/// </summary>
internal static class RuleEdits
{
    /// <summary>The side pack's name for a toast ("Satchel", "Satchel (2)" when two share it), or "pack 3".</summary>
    internal static string PackName(SortView view, int number) => PackNames.Label(view, number);

    /// <summary>
    /// "All &lt;kind&gt;": class <paramref name="cls"/> goes to side pack <paramref name="number"/>,
    /// into one of its rules (a new one when it has none with room), and out of every other pack's.
    /// </summary>
    public static EditResult SendKind(List<SortRule> rules, SortView view, int number, int cls)
    {
        if (number < 1 || number > view.Packs.Count) return EditResult.Nothing("That pack isn't carried now.");
        string label = Kinds.Label(cls);
        string pack = PackName(view, number);
        bool already = false;
        foreach (SortRule r in rules)
            if (r.On && r.HasClass(cls) && SortPanel.ShownPack(view, r) == number) { already = true; break; }

        var taken = new List<string>();
        for (int i = rules.Count - 1; i >= 0; i--)
        {
            SortRule r = rules[i];
            int at = SortPanel.ShownPack(view, r);
            if (at == number || !r.HasClass(cls)) continue;
            r.RemoveClass(cls);
            string from = at > 0 ? PackName(view, at) : "a pack you don't carry";
            if (!taken.Contains(from)) taken.Add(from);
            if (r.IsEmpty) rules.RemoveAt(i);
        }
        if (already && taken.Count == 0) return EditResult.Nothing($"{label} already go to {pack}.");
        if (!already)
        {
            SortRule? target = null;
            foreach (SortRule r in rules)
                if (SortPanel.ShownPack(view, r) == number && r.On && r.Classes.Count > 0 && r.Classes.Count < SortRule.MaxClasses) { target = r; break; }
            if (target == null)
                foreach (SortRule r in rules)
                    if (SortPanel.ShownPack(view, r) == number && r.On && r.Classes.Count < SortRule.MaxClasses) { target = r; break; }
            if (target == null)
            {
                if (rules.Count >= SortStore.MaxRules)
                    return new EditResult(taken.Count > 0, $"There is no room for another rule (the limit is {SortStore.MaxRules}).");
                target = new SortRule();
                SortPanel.SetPack(target, view, number);
                rules.Add(target);
            }
            target.AddClass(cls);
        }
        string fromText = taken.Count == 0 ? string.Empty : " (taken from " + string.Join(", ", taken) + ")";
        return new EditResult(true, $"All {label.ToLowerInvariant()} now go to {pack}{fromText}. Nothing has moved yet.");
    }

    /// <summary>
    /// "Overflow": class <paramref name="cls"/> stays with the pack that has it, and side pack
    /// <paramref name="number"/> takes it when that pack is full (the planner spills into the next
    /// pack with the same rule). A new rule at the end of the list, so the pack that has the kind
    /// keeps it first. Nothing when no other carried pack has the kind (that is "All &lt;kind&gt;").
    /// </summary>
    public static EditResult AddOverflow(List<SortRule> rules, SortView view, int number, int cls)
    {
        if (number < 1 || number > view.Packs.Count) return EditResult.Nothing("That pack isn't carried now.");
        string label = Kinds.Label(cls);
        int firstPack = 0;
        foreach (SortRule r in rules)
            if (r.On && r.HasClass(cls)) { firstPack = SortPanel.ShownPack(view, r); break; }
        if (firstPack <= 0) return EditResult.Nothing($"No pack you carry takes {label.ToLowerInvariant()} yet: add the kind to a pack first.");
        if (firstPack == number) return EditResult.Nothing($"{label} already go to {PackName(view, number)}.");
        foreach (SortRule r in rules)
            if (r.On && r.HasClass(cls) && SortPanel.ShownPack(view, r) == number)
                return EditResult.Nothing($"{PackName(view, number)} already takes {label.ToLowerInvariant()} when {PackName(view, firstPack)} is full.");
        if (rules.Count >= SortStore.MaxRules) return EditResult.Nothing($"There is no room for another rule (the limit is {SortStore.MaxRules}).");
        var rule = new SortRule();
        SortPanel.SetPack(rule, view, number);
        rule.AddClass(cls);
        rules.Add(rule);
        return new EditResult(true, $"{label} go to {PackName(view, firstPack)}, and to {PackName(view, number)} when it is full. Nothing has moved yet.");
    }

    /// <summary>
    /// "Only &lt;name&gt;": the item's name (without a "(100)" count) goes into side pack
    /// <paramref name="number"/>'s rules, and the same name comes out of every other pack's.
    /// </summary>
    public static EditResult SendName(List<SortRule> rules, SortView view, int number, SortItem item)
    {
        if (number < 1 || number > view.Packs.Count) return EditResult.Nothing("That pack isn't carried now.");
        string entry = AddSelectedRules.EntryFor(item.Name);
        if (entry.Length == 0) return EditResult.Nothing("That item has no name the client can read yet; try again in a moment.");
        bool removed = false;
        for (int i = rules.Count - 1; i >= 0; i--)
        {
            SortRule r = rules[i];
            if (SortPanel.ShownPack(view, r) == number || !r.HasName(entry)) continue;
            r.RemoveName(entry);
            removed = true;
            if (r.IsEmpty) rules.RemoveAt(i);
        }
        var sel = new SelectedItem { Kind = SelectionKind.Item, Id = item.Id, Name = item.Name, Item = item };
        AddOutcome o = AddSelectedRules.Apply(rules, view, number, sel);
        string pack = PackName(view, number);
        return o.Status switch
        {
            AddStatus.Added => new EditResult(true, $"\"{entry}\" now goes to {pack}. Nothing has moved yet."),
            AddStatus.AlreadyThere when removed => new EditResult(true, $"\"{entry}\" now goes to {pack}. Nothing has moved yet."),
            AddStatus.AlreadyThere => EditResult.Nothing($"\"{entry}\" already goes to {pack}."),
            _ => new EditResult(removed, o.Message),
        };
    }

    /// <summary>
    /// A name typed into "+ Add a rule" (0.6.1): items whose name has <paramref name="text"/> in it go
    /// to side pack <paramref name="number"/>, and the same name comes out of every other pack's rules.
    /// No item needed (before, a name rule took an item dragged on, or the one selected in the game).
    /// </summary>
    public static EditResult SendNameText(List<SortRule> rules, SortView view, int number, string text)
    {
        if (number < 1 || number > view.Packs.Count) return EditResult.Nothing("That pack isn't carried now.");
        string entry = (text ?? string.Empty).Trim();
        if (entry.Length > SortRule.MaxNameLength) entry = entry.Substring(0, SortRule.MaxNameLength).Trim();
        if (entry.Length == 0) return EditResult.Nothing("Type part of a name first.");
        string pack = PackName(view, number);
        SortRule? here = rules.Find(r => SortPanel.ShownPack(view, r) == number && r.HasName(entry));
        if (here != null && !here.On) return EditResult.Nothing($"\"{entry}\" is in a rule of {pack} that is turned off: click it to turn it on.");
        bool removed = false;
        for (int i = rules.Count - 1; i >= 0; i--)
        {
            SortRule r = rules[i];
            if (SortPanel.ShownPack(view, r) == number || !r.HasName(entry)) continue;
            r.RemoveName(entry);
            removed = true;
            if (r.IsEmpty) rules.RemoveAt(i);
        }
        if (here != null)
            return removed
                ? new EditResult(true, $"\"{entry}\" now goes to {pack} only. Nothing has moved yet.")
                : EditResult.Nothing($"\"{entry}\" already goes to {pack}.");
        SortRule? target = null;
        // This pack's rule that is on and already lists names, else a new rule, else any of its rules with room.
        foreach (SortRule r in rules)
            if (SortPanel.ShownPack(view, r) == number && r.On && r.Names.Count > 0 && r.Names.Count < SortRule.MaxNames) { target = r; break; }
        if (target == null && rules.Count < SortStore.MaxRules)
        {
            target = new SortRule();
            SortPanel.SetPack(target, view, number);
            rules.Add(target);
        }
        if (target == null)
            foreach (SortRule r in rules)
                if (SortPanel.ShownPack(view, r) == number && r.On && r.Names.Count < SortRule.MaxNames) { target = r; break; }
        if (target == null || !target.AddName(entry))
            return removed
                ? new EditResult(true, $"\"{entry}\" left the other packs' rules, but {pack}'s rules are full.")
                : EditResult.Nothing($"There is no room in {pack}'s rules.");
        return new EditResult(true, $"Items with \"{entry}\" in their name now go to {pack}. Nothing has moved yet.");
    }

    /// <summary>A chip's ×: the kind or name comes out of rule <paramref name="uid"/> (the rule goes when nothing is left in it).</summary>
    public static EditResult RemoveEntry(List<SortRule> rules, SortView view, int uid, bool isName, int cls, string name)
    {
        int i = rules.FindIndex(r => r.Uid == uid);
        if (i < 0) return EditResult.Nothing("That rule is gone already.");
        SortRule r = rules[i];
        int at = SortPanel.ShownPack(view, r);
        string where = at > 0 ? PackName(view, at) : "that pack";
        bool done = isName ? r.RemoveName(name) : r.RemoveClass(cls);
        if (!done) return EditResult.Nothing("That is gone from the rule already.");
        if (r.IsEmpty) rules.RemoveAt(i);
        return new EditResult(true, isName
            ? $"\"{name}\" no longer goes to {where}."
            : $"{Kinds.Label(cls)} no longer go to {where}.");
    }

    /// <summary>A faded chip's click: its rule is turned on.</summary>
    public static EditResult TurnOn(List<SortRule> rules, int uid)
    {
        SortRule? r = rules.Find(x => x.Uid == uid);
        if (r == null) return EditResult.Nothing("That rule is gone already.");
        if (r.On) return EditResult.Nothing("That rule is on already.");
        r.On = true;
        return new EditResult(true, $"Rule turned on: {SortRules.Describe(r)}.");
    }

    /// <summary>"Use this layout": the layout's rules, fitted to the packs carried, replace the character's.</summary>
    public static EditResult UseLayout(List<SortRule> rules, SortInventory? inv, StarterLayout layout)
    {
        if (inv == null) return EditResult.Nothing("Your packs aren't read yet; try again in a moment.");
        if (inv.Packs.Count == 0) return EditResult.Nothing("You carry no side packs, so a layout has nowhere to put things.");
        LayoutPlan plan = StarterLayouts.Fit(layout, inv);
        rules.Clear();
        rules.AddRange(plan.Rules);
        return new EditResult(true, $"Rules set from {layout.Name}. Press Sort now when you're ready.");
    }
}

/// <summary>
/// The window's tick-side state: the toast after a change and the one Undo it offers (the rules as
/// they were before the last change). Pure; the plugin owns one per session.
/// </summary>
internal sealed class ScreenSession
{
    public const long ToastMs = 6000;

    private List<SortRule>? _undo;
    private int _seq;

    /// <summary>The toast showing, or null.</summary>
    public ToastModel? Toast { get; private set; }

    /// <summary>Bumped by every change the window should show at once.</summary>
    public int Version { get; private set; }

    public bool CanUndo => _undo != null;

    public void Reset()
    {
        _undo = null;
        Toast = null;
        Version++;
    }

    /// <summary>
    /// Runs a rule change on <paramref name="rules"/>: on a change the rules as they were become the
    /// Undo and the toast offers it; otherwise the toast just says why nothing changed. True on a change.
    /// </summary>
    public bool Edit(List<SortRule> rules, Func<List<SortRule>, EditResult> change, long nowMs)
    {
        List<SortRule> before = rules.ConvertAll(r => r.Clone());
        EditResult result = change(rules);
        if (result.Changed) _undo = before;
        Show(result.Text, result.Changed, nowMs);
        return result.Changed;
    }

    /// <summary>Shows <paramref name="text"/> (with Undo when <paramref name="undo"/> and there is one).</summary>
    public void Show(string text, bool undo, long nowMs)
    {
        Version++;
        if (string.IsNullOrEmpty(text)) { Toast = null; return; }
        Toast = new ToastModel { Text = text, CanUndo = undo && _undo != null, Seq = ++_seq, UntilMs = nowMs + ToastMs };
    }

    /// <summary>Undo: the rules as they were before the last change. False when there is nothing to undo.</summary>
    public bool Undo(List<SortRule> rules, long nowMs)
    {
        if (_undo == null) return false;
        rules.Clear();
        foreach (SortRule r in _undo) rules.Add(r.Clone());
        _undo = null;
        Show("Rules put back as they were.", false, nowMs);
        return true;
    }

    /// <summary>The toast is past its time (or dismissed).</summary>
    public void Expire(long nowMs)
    {
        if (Toast != null && nowMs >= Toast.UntilMs) { Toast = null; Version++; }
    }
}

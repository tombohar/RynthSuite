using System;
using System.Collections.Generic;
using RynthCore.Plugin.RynthSort.Sorting;
using RynthCore.Plugin.RynthSort.Ui;

namespace RynthCore.Plugin.RynthSort.Views;

// The Packs page: "what goes in this pack", one section per side pack.
internal sealed partial class SortPanel
{
    private const float WhereIcon = 20f, KindIcon = 18f;
    private const int KindIcons = 8;

    // The last big rule change (a layout, a kind taken from another pack, a rule removed or merged), for Undo.
    private List<SortRule>? _undo;
    private string _undoWhat = string.Empty;
    private Dictionary<SortRule, (int Here, int Coming)> _counts = new();

    /// <summary>Whether Undo has something to put back, and what.</summary>
    public string UndoWhat => _undo != null ? _undoWhat : string.Empty;

    private void DrawPacksTab(UiWindow w, SortView view, List<SortRule> rules)
    {
        SortInventory? inv = _c.LastInventory;
        _counts = RuleCounts(rules, view, inv, _preview);

        if (_undo != null)
        {
            w.SmallButton("Undo: " + _undoWhat, "undo", Undo);
            w.Tooltip("Put the rules back as they were before that change. Nothing moves.");
        }
        DrawLayoutNote(w);
        if (rules.Count == 0)
        {
            w.PushStyleColor(UiFormat.ColText, UiColors.Soft);
            w.TextWrapped("No rules yet. A starter layout fills them in one click (you see where things go first), "
                          + "or pick a kind of item for each pack below.");
            w.PopStyleColor();
            w.SmallButton("Pick a starter layout", "go.layouts", () => ShowTab(PanelTab.Layouts));
            w.Tooltip("Shows the starter layouts: Everyday, Hunting, Crafting and Just the basics");
        }

        w.TextDisabled($"Main pack ({view.Main.Used}/{view.Main.Capacity}){OrderNote(view, view.Main, " - ")}");
        w.Tooltip("Items no rule names stay in the main pack");

        string numbering = NumberingNote(view);
        if (numbering.Length > 0)
        {
            w.PushStyleColor(UiFormat.ColText, UiColors.Grey);
            w.TextWrapped(numbering);
            w.PopStyleColor();
        }

        Dictionary<int, List<SortRule>> byPack = Group(rules, view);
        Dictionary<int, List<string>> conflicts = Conflicts(rules, view);
        for (int i = 1; i <= view.Packs.Count; i++)
            DrawPack(w, view, inv, rules, i, byPack.TryGetValue(i, out List<SortRule>? list) ? list : new List<SortRule>(),
                     conflicts.TryGetValue(i, out List<string>? c) ? c : null);
        if (byPack.TryGetValue(0, out List<SortRule>? missing)) DrawMissing(w, view, rules, missing);
        if (view.Packs.Count == 0) w.TextDisabled("No side packs: items stay in the main pack.");
    }

    // ── Pack sections ───────────────────────────────────────────────────────

    private void DrawPack(UiWindow w, SortView view, SortInventory? inv, List<SortRule> rules, int number, List<SortRule> packRules, List<string>? conflicts)
    {
        PackLine p = view.Packs[number - 1];
        bool full = view.Full.Contains(number);
        List<string> warnings = FallbackWarnings(view, number);
        if (conflicts != null) warnings.AddRange(conflicts);
        bool attention = full || warnings.Count > 0;
        if (attention) w.PushStyleColor(UiFormat.ColText, UiColors.Amber);
        bool open = w.CollapsingHeader($"Pack {number}: {p.Label}", $"pack.{p.Id:X8}", defaultOpen: true);
        if (attention) w.PopStyleColor();
        w.Tooltip(PackHeaderTooltip(view, number));
        if (!open) return;

        w.Indent(RowIndent);
        DrawWhere(w, view, p, number);
        DrawContents(w, inv, p);
        if (full) w.TextColored(UiColors.Amber, "This pack is full: items for it wait where they are");
        foreach (string line in warnings) w.TextColored(UiColors.Amber, line);

        packRules.Sort(SortRules.CompareForList);
        if (packRules.Count > 0)
        {
            w.TextDisabled("Goes here:");
            foreach (SortRule r in packRules) DrawRule(w, view, rules, r);
        }
        else w.TextDisabled("Nothing goes here yet.");

        uint id = p.Id;
        DrawKindPicker(w, view, rules, number, packRules);
        w.SmallButton("+ Add rule", $"add.{id:X8}", () => AddRule(rules, view, number));
        w.Tooltip("A new rule for this pack, to fill in: pick kinds of items, or type part of a name (\"Opal\"). A name beats a kind.");
        w.SameLine();
        w.SmallButton("+ Add selected", $"addsel.{id:X8}", () => AddSelected(number));
        w.Tooltip("Select an item in your inventory, then press this: its name goes into this pack's rules (\"name has ...\"), "
                  + "so items called that go here. A count like \"(100)\" is left out. It says if the pack already takes the item or another pack does.");
        // A second line: these two empty or change the pack, so they sit apart from the adds.
        w.SmallButton("Empty this pack", $"unpack.{id:X8}", () => _c.StartUnpack(number));
        w.Tooltip($"Move everything in pack {number} into the main pack. Turns auto-sort off first, so nothing moves back. Also: /rsort unpack {number}");
        int on = 0;
        foreach (SortRule r in packRules) if (r.On && !r.IsEmpty) on++;
        if (on >= 2)
        {
            w.SameLine();
            w.SmallButton("Merge rules", $"merge.{id:X8}", () => MergeRules(rules, packRules));
            w.Tooltip("Make this pack's rules (the ones that are on) one rule with all their kinds and names");
        }
        if (_notePack == id && _note.Length > 0 && Now() < _noteUntilMs)
        {
            w.PushStyleColor(UiFormat.ColText, _noteGood ? UiColors.Green : UiColors.Amber);
            w.TextWrapped(_note);
            w.PopStyleColor();
        }
        string order = OrderNote(view, p, "");
        if (order.Length > 0) w.TextDisabled(order);
        w.Unindent(RowIndent);
    }

    /// <summary>The pack's first line: its icon, Select in game, and where it is in the pack column.</summary>
    private void DrawWhere(UiWindow w, SortView view, PackLine p, int number)
    {
        if (Icons())
        {
            w.Image(UiFormat.IconKindObject, p.Id, WhereIcon, WhereIcon);
            w.SameLine();
        }
        if (CanSelect())
        {
            uint id = p.Id;
            string name = p.Name;
            w.SmallButton("Select in game", $"select.{id:X8}", () =>
            {
                _selectedPack = id;
                _selectNote = SelectInGame(id) ? $"Selected: the game has your {name} selected now" : "The game didn't take the selection";
            });
            w.Tooltip(SelectTooltip);
            w.SameLine();
        }
        w.PushStyleColor(UiFormat.ColText, UiColors.Grey);
        w.TextWrapped(WhereText(view, p, number));
        if (_selectedPack == p.Id && _selectNote.Length > 0) w.TextWrapped(_selectNote);
        w.PopStyleColor();
    }

    /// <summary>What is in the pack, as a strip of item icons (the commonest kinds; each icon's tooltip names it).</summary>
    private void DrawContents(UiWindow w, SortInventory? inv, PackLine p)
    {
        if (!Icons() || inv == null) return;
        int index = inv.IndexOf(p.Id);
        if (index == 0) return;
        SortPack pack = inv.Packs[index - 1];
        if (pack.Items.Count == 0) return;
        List<(SortItem First, int Count)> kinds = Kinds(pack, KindIcons);
        for (int i = 0; i < kinds.Count; i++)
        {
            if (i > 0) w.SameLine();
            w.Image(UiFormat.IconKindObject, kinds[i].First.Id, KindIcon, KindIcon);
            string name = kinds[i].First.Name.Length > 0 ? kinds[i].First.Name : "(not known yet)";
            w.Tooltip(kinds[i].Count > 1 ? $"{name}: {kinds[i].Count}" : name);
        }
    }

    /// <summary>
    /// "Add a kind of item...": the kind goes to this pack. If another pack's rule had it, it is
    /// taken from there (pack-centric: what goes here is what this pack says), with Undo.
    /// </summary>
    private void DrawKindPicker(UiWindow w, SortView view, List<SortRule> rules, int number, List<SortRule> packRules)
    {
        var offered = new List<int>();
        var labels = new List<string> { "Add a kind of item..." };
        foreach (int cls in ObjectClasses.Choices)
        {
            bool here = false;
            foreach (SortRule r in packRules) if (r.On && r.HasClass(cls)) { here = true; break; }
            if (here) continue;
            offered.Add(cls);
            labels.Add(ObjectClasses.Label(cls));
        }
        if (offered.Count == 0) return;
        uint id = view.Packs[number - 1].Id;
        w.Combo("", $"kind.{id:X8}", 0, labels, v =>
        {
            if (v >= 1 && v <= offered.Count) SendKindHere(rules, view, number, offered[v - 1]);
        });
        w.Tooltip("Pick a kind of item to send to this pack. If another pack took that kind, it comes here instead (Undo puts it back).");
    }

    /// <summary>Sends class <paramref name="cls"/> to side pack <paramref name="number"/>: into its first rule with room, taken out of other packs' rules.</summary>
    internal void SendKindHere(List<SortRule> rules, SortView view, int number, int cls)
    {
        var before = Snapshot(rules);
        var taken = new List<int>();
        foreach (SortRule r in rules)
        {
            int pack = ShownPack(view, r);
            if (pack == number || !r.On || !r.HasClass(cls)) continue;
            r.RemoveClass(cls);
            if (pack > 0 && !taken.Contains(pack)) taken.Add(pack);
        }
        rules.RemoveAll(r => r.IsEmpty && !ReferenceEquals(r, _selected) && before.Exists(b => b.Uid == r.Uid && !b.Copy.IsEmpty));

        SortRule? target = null;
        foreach (SortRule r in rules)
            if (ShownPack(view, r) == number && r.On && r.Classes.Count > 0 && r.Classes.Count < SortRule.MaxClasses) { target = r; break; }
        if (target == null)
            foreach (SortRule r in rules)
                if (ShownPack(view, r) == number && r.On && r.Classes.Count < SortRule.MaxClasses) { target = r; break; }
        if (target == null)
        {
            if (rules.Count >= SortStore.MaxRules) return;
            target = new SortRule();
            SetPack(target, view, number);
            rules.Add(target);
        }
        target.AddClass(cls);
        string label = ObjectClasses.Plural(cls);
        KeepUndo(before, $"{label} to pack {number}");
        _notePack = view.Packs[number - 1].Id;
        _note = taken.Count > 0
            ? $"{label} now go here (taken from {(taken.Count == 1 ? "pack " + taken[0] : "packs " + string.Join(", ", taken))}). Nothing has moved yet."
            : $"{label} now go here. Nothing has moved yet.";
        _noteGood = true;
        _noteUntilMs = Now() + NoteMs;
        _save();
    }

    private void DrawMissing(UiWindow w, SortView view, List<SortRule> rules, List<SortRule> missing)
    {
        w.PushStyleColor(UiFormat.ColText, UiColors.Amber);
        bool open = w.CollapsingHeader($"Rules for packs you don't have ({missing.Count})", "missing", defaultOpen: true);
        w.PopStyleColor();
        if (!open) return;
        w.Indent(RowIndent);
        w.TextWrapped("Their items stay where they are. Pick one of your packs in a rule's editor, or remove the rule.");
        var groups = new List<List<SortRule>>();
        foreach (SortRule r in missing)
        {
            List<SortRule>? g = groups.Find(x => SamePackOf(x[0], r));
            if (g == null) groups.Add(g = new List<SortRule>());
            g.Add(r);
        }
        foreach (List<SortRule> g in groups)
        {
            SortRule first = g[0];
            w.TextColored(UiColors.Amber, first.PackNote.Length > 0 ? $"A pack you don't have (it was {first.PackNote}):" : $"Pack {first.Pack}, which you don't have:");
            g.Sort(SortRules.CompareForList);
            foreach (SortRule r in g) DrawRule(w, view, rules, r);
        }
        w.Unindent(RowIndent);
    }

    private static bool SamePackOf(SortRule a, SortRule b) =>
        a.PackId != 0 || b.PackId != 0 ? a.PackId == b.PackId : a.Pack == b.Pack;

    // ── Rules ───────────────────────────────────────────────────────────────

    private void DrawRule(UiWindow w, SortView view, List<SortRule> rules, SortRule r)
    {
        string k = "r" + r.Uid;
        w.Checkbox("", k + ".on", r.On, v => { r.On = v; _save(); });
        w.Tooltip(r.On ? "Rule on: click to turn it off" : "Rule off: click to turn it on");
        w.SameLine();
        bool selected = ReferenceEquals(_selected, r);
        w.Selectable(Shown(SortRules.Describe(r)) + CountWords(r), k + ".row", selected, () =>
        {
            _selected = ReferenceEquals(_selected, r) ? null : r;
            _nameDraft = string.Empty;
        });
        w.Tooltip(selected ? "Click to close the editor" : "Click to change this rule");
        if (selected) DrawEditor(w, view, rules, r);
    }

    /// <summary>"  (12 here, 3 to move in)" after a rule's row, or nothing.</summary>
    private string CountWords(SortRule r)
    {
        if (!r.On || !_counts.TryGetValue(r, out var c) || (c.Here == 0 && c.Coming == 0)) return string.Empty;
        if (c.Coming == 0) return $"  ({c.Here} here)";
        return c.Here == 0 ? $"  ({c.Coming} to move in)" : $"  ({c.Here} here, {c.Coming} to move in)";
    }

    private void DrawEditor(UiWindow w, SortView view, List<SortRule> rules, SortRule r)
    {
        string k = "r" + r.Uid;
        w.Indent(EditorIndent);

        List<int> classes = SortRules.SortedClasses(r);
        if (classes.Count > 0)
        {
            w.TextDisabled("Classes:");
            for (int i = 0; i < classes.Count; i++)
            {
                if (i % ChipsPerLine != 0 || i == 0) w.SameLine();
                int cls = classes[i];
                string label = ObjectClasses.Label(cls);
                w.SmallButton(label + "  x", $"{k}.c{cls}", () => { r.RemoveClass(cls); _save(); });
                w.Tooltip($"Take {label} out of this rule");
            }
        }
        var offered = new List<int>();
        var labels = new List<string> { "Add a class..." };
        foreach (int cls in ObjectClasses.Choices)
        {
            if (r.HasClass(cls)) continue;
            offered.Add(cls);
            labels.Add(ObjectClasses.Label(cls));
        }
        if (offered.Count > 0 && r.Classes.Count < SortRule.MaxClasses)
            w.Combo("Class", k + ".addclass", 0, labels, v =>
            {
                if (v >= 1 && v <= offered.Count && r.AddClass(offered[v - 1])) _save();
            });

        List<string> names = SortRules.SortedNames(r);
        if (names.Count > 0)
        {
            w.TextDisabled("Names:");
            for (int i = 0; i < names.Count; i++)
            {
                if (i % ChipsPerLine != 0 || i == 0) w.SameLine();
                string name = names[i];
                w.SmallButton($"\"{Shown(name)}\"  x", $"{k}.n{i}", () => { r.RemoveName(name); _save(); });
                w.Tooltip($"Take \"{name}\" out of this rule");
            }
        }
        if (r.Names.Count < SortRule.MaxNames)
        {
            w.InputText("Name has", k + ".draft", _nameDraft, SortRule.MaxNameLength, (text, enter) =>
            {
                _nameDraft = text;
                if (enter) AddDraft(r);
            }, UiFormat.InputEnterReturnsTrue);
            w.Tooltip("Part of an item's name, any case: \"Opal\" takes every item with Opal in its name. Enter or Add puts it in the rule.");
            w.SameLine();
            w.SmallButton("Add", k + ".addname", () => AddDraft(r));
        }

        int shown = ShownPack(view, r);
        w.Combo("Pack", k + ".pack", Math.Max((shown > 0 ? shown : r.Pack) - 1, 0), PackChoices(view, r.Pack), v =>
        {
            SetPack(r, view, v + 1);
            _save();
        });
        w.Tooltip("Where this rule sends items. " + PackTooltip(view));
        w.SmallButton("Remove rule", k + ".remove", () =>
        {
            KeepUndo(Snapshot(rules), "rule removed");
            rules.Remove(r);
            if (ReferenceEquals(_selected, r)) _selected = null;
            _save();
        });
        w.Tooltip("Take this rule out (Undo brings it back). Items it sent stay where they are.");
        w.Unindent(EditorIndent);
    }

    private void AddDraft(SortRule r)
    {
        if (r.AddName(_nameDraft)) _save();
        _nameDraft = string.Empty;
    }

    /// <summary>
    /// "+ Add selected" (and /rsort add): the selected item's name goes into side pack
    /// <paramref name="number"/>'s rules (<see cref="AddSelectedRules"/>); what happened is shown under the pack's buttons.
    /// </summary>
    public AddOutcome AddSelected(int number)
    {
        SortView view = _c.Planner.View;
        AddOutcome o = AddSelectedRules.Apply(_c.Data.Rules, view, number, ReadSelected());
        if (o.Rule != null)
        {
            _selected = o.Rule;
            _nameDraft = string.Empty;
            _save();
        }
        _notePack = number >= 1 && number <= view.Packs.Count ? view.Packs[number - 1].Id : 0;
        _note = o.Message;
        _noteGood = o.Status == AddStatus.Added;
        _noteUntilMs = Now() + NoteMs;
        if (_tab != PanelTab.Packs) ShowTab(PanelTab.Packs);
        return o;
    }

    private void AddRule(List<SortRule> rules, SortView view, int number)
    {
        if (rules.Count >= SortStore.MaxRules) return;
        var added = new SortRule();
        SetPack(added, view, number);
        rules.Add(added);
        _selected = added;
        _nameDraft = string.Empty;
        _save();
    }

    /// <summary>The pack's rules that are on become one (the first A-Z keeps its place in the list, so a tie still goes its way).</summary>
    private void MergeRules(List<SortRule> rules, List<SortRule> packRules)
    {
        var before = Snapshot(rules);
        SortRule? keep = null;
        foreach (SortRule r in rules)
        {
            if (!packRules.Contains(r) || !r.On || r.IsEmpty) continue;
            if (keep == null) { keep = r; continue; }
            keep.TakeEntriesOf(r);
        }
        if (keep == null) return;
        rules.RemoveAll(r => !ReferenceEquals(r, keep) && packRules.Contains(r) && r.On && !r.IsEmpty);
        KeepUndo(before, "rules merged");
        _selected = keep;
        _save();
    }

    // ── Undo ────────────────────────────────────────────────────────────────

    private readonly record struct Kept(int Uid, SortRule Copy);

    private static List<Kept> Snapshot(List<SortRule> rules) => rules.ConvertAll(r => new Kept(r.Uid, r.Clone()));

    private void KeepUndo(List<Kept> before, string what)
    {
        _undo = before.ConvertAll(k => k.Copy);
        _undoWhat = what;
    }

    /// <summary>The rules as they were before the last big change (nothing moves).</summary>
    internal void Undo()
    {
        if (_undo == null) return;
        List<SortRule> rules = _c.Data.Rules;
        rules.Clear();
        foreach (SortRule r in _undo) rules.Add(r.Clone());
        _undo = null;
        _undoWhat = string.Empty;
        _selected = null;
        _layoutNote = "Rules put back as they were.";
        _layoutNoteGood = true;
        _layoutNoteUntilMs = Now() + NoteMs;
        _save();
    }
}

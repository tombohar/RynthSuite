using System;
using System.Collections.Generic;
using RynthCore.Plugin.RynthSort.Sorting;
using RynthCore.Plugin.RynthSort.Ui;
using RynthCore.Plugin.Shared;

namespace RynthCore.Plugin.RynthSort.Views;

/// <summary>
/// The RynthSort window: the auto-sort box and status, the job buttons, then one collapsing
/// section per side pack ("Pack 1: Sack (12/24) - Components", top pack first) holding that
/// pack's warnings and its rules (A-Z), a section for rules whose pack isn't carried, and the
/// settings. A rule is one row (on/off box, its classes and names); selecting it opens its
/// editor beneath: the classes and names as chips (click one to take it out), a class picker and
/// a name box to add, the pack it sends to, and Remove. Recorded on the pump thread, replayed by
/// the engine.
/// </summary>
internal sealed class SortPanel
{
    private const float RowIndent = 6f, EditorIndent = 22f;
    private const int ChipsPerLine = 3;

    private readonly SortController _c;
    private readonly Action _save;
    private SortRule? _selected;
    private string _nameDraft = string.Empty;

    /// <summary>Extra warnings the plugin knows about (the Lua script set to start too).</summary>
    public Func<IReadOnlyList<string>> Warnings = () => Array.Empty<string>();
    /// <summary>The character (blank before login).</summary>
    public Func<string> Character = () => string.Empty;

    public SortPanel(SortController c, Action save)
    {
        _c = c;
        _save = save;
    }

    /// <summary>The rule whose editor is open, or null.</summary>
    public SortRule? Selected => _selected;

    public void Draw(UiWindow w)
    {
        if (Character().Length == 0)
        {
            w.TextDisabled("Log in to sort your packs.");
            return;
        }
        SortSettings s = _c.Data.Settings;
        List<SortRule> rules = _c.Data.Rules;
        if (_selected != null && !rules.Contains(_selected)) _selected = null;
        SortView view = _c.Planner.View;

        w.Checkbox("Auto-sort", "auto", s.Auto, v => _c.SetAuto(v));
        w.Tooltip("Keeps every pack tidy: new items go to their pack after the settle time, items in the wrong pack move to theirs, "
                  + "stacks of one kind are joined and items are put in order (A-Z) inside each pack");
        w.SameLine();
        w.TextColored(_c.Paused ? UiColors.Amber : UiColors.Grey, _c.Status);
        if (view.Waiting > 0)
        {
            w.SameLine();
            w.TextDisabled($"({view.Waiting} waiting)");
        }

        w.Button("Sort now", "job.all", () => _c.StartJob(SortScope.All));
        w.Tooltip("Sort every pack now, without waiting for new items to settle");
        w.SameLine();
        w.Button("Clean packs", "job.clean", () => _c.StartJob(SortScope.Clean));
        w.Tooltip("Sort now, and side-pack items no rule wants go back to the main pack");
        w.SameLine();
        w.Button("Empty side packs", "job.unpack", () => _c.StartUnpack(null));
        w.Tooltip("Move everything from your side packs into the main pack. Turns auto-sort off first, so nothing moves back.");

        w.TextDisabled($"Main pack ({view.Main.Used}/{view.Main.Capacity}){OrderNote(view, view.Main, " - ")}");
        w.Tooltip("Items no rule names stay in the main pack");
        if (view.MainFull) w.TextColored(UiColors.Amber, "The main pack is full");
        if (s.Auto && _c.LastBot is RynthAiActivity bot && (bot & RynthAiActivity.AutoCram) != 0 && (bot & RynthAiActivity.MacroRunning) != 0)
            w.TextColored(UiColors.Amber, "RynthAi's AutoCram also moves loot out of the main pack; turn one of them off");
        foreach (string warning in Warnings()) w.TextColored(UiColors.Amber, warning);

        Dictionary<int, List<SortRule>> byPack = Group(rules, view);
        Dictionary<int, List<string>> conflicts = Conflicts(rules, view);
        for (int i = 1; i <= view.Packs.Count; i++)
            DrawPack(w, view, rules, i, byPack.TryGetValue(i, out List<SortRule>? list) ? list : new List<SortRule>(),
                     conflicts.TryGetValue(i, out List<string>? c) ? c : null);
        if (byPack.TryGetValue(0, out List<SortRule>? missing)) DrawMissing(w, view, rules, missing);
        if (view.Packs.Count == 0) w.TextDisabled("No side packs: items stay in the main pack.");

        if (w.CollapsingHeader("Settings", "settings"))
        {
            w.SliderInt("Settle (s)", "settle", s.SettleSec, SortSettings.SettleMin, SortSettings.SettleMax, v => { s.SettleSec = v; _save(); });
            w.Tooltip("How long a new item sits in the main pack before it is moved");
            w.SliderInt("Gap (ms)", "gap", s.GapMs, SortSettings.GapMin, SortSettings.GapMax, v => { s.GapMs = v; _save(); });
            w.Tooltip("Auto-sort's pause between two moves, to leave the bot room. Sort now, Clean and Empty don't wait.");
            w.Checkbox("Sort during fights", "combat", s.SortInCombat, v => { s.SortInCombat = v; _save(); });
            w.Tooltip("Off: nothing moves while RynthAi fights. On: moves go between its attacks and casts (never while one is unanswered)");
            w.Checkbox("Put items in order inside packs", "order", s.OrderInPacks, v => { s.OrderInPacks = v; _save(); });
            w.Tooltip("Like items side by side, A-Z by name, in every pack. Off: items only go to their packs (and stacks are still joined).");
            w.Checkbox("Group by kind first", "orderkind", s.OrderKindFirst, v => { s.OrderKindFirst = v; _save(); });
            w.Tooltip("Order by kind (Armor, Gem, Spell Component ...) and then A-Z by name, instead of A-Z by name only");
            if (!(_c.Abilities.ReadOrder && _c.Abilities.PlaceMoves))
                w.TextWrapped("This RynthCore can't read the order inside packs (it needs plugin API 80): items go to their packs but aren't put in order.");
        }

        if (_c.Log.Count > 0)
        {
            w.Separator();
            foreach (string line in _c.Log) w.TextDisabled(line);
        }
    }

    // ── Pack sections ───────────────────────────────────────────────────────

    private void DrawPack(UiWindow w, SortView view, List<SortRule> rules, int number, List<SortRule> packRules, List<string>? conflicts)
    {
        PackLine p = view.Packs[number - 1];
        bool full = view.Full.Contains(number);
        List<string> warnings = FallbackWarnings(view, number);
        if (conflicts != null) warnings.AddRange(conflicts);
        bool attention = full || warnings.Count > 0;
        if (attention) w.PushStyleColor(UiFormat.ColText, UiColors.Amber);
        bool open = w.CollapsingHeader($"Pack {number}: {p.Label}", $"pack.{p.Id:X8}", defaultOpen: true);
        if (attention) w.PopStyleColor();
        w.Tooltip(PackTooltip(view));
        if (!open) return;

        w.Indent(RowIndent);
        if (full) w.TextColored(UiColors.Amber, "This pack is full: items for it wait where they are");
        foreach (string line in warnings) w.TextColored(UiColors.Amber, line);
        packRules.Sort(SortRules.CompareForList);
        foreach (SortRule r in packRules) DrawRule(w, view, rules, r);
        if (packRules.Count == 0) w.TextDisabled("No rules send items here.");

        uint id = p.Id;
        w.SmallButton("+ Add rule", $"add.{id:X8}", () => AddRule(rules, view, number));
        w.Tooltip("A rule sends items to this pack: items of any of its classes, or with any of its names in their name. A name beats a class.");
        int on = 0;
        foreach (SortRule r in packRules) if (r.On && !r.IsEmpty) on++;
        if (on >= 2)
        {
            w.SameLine();
            w.SmallButton("Merge rules", $"merge.{id:X8}", () => MergeRules(rules, packRules));
            w.Tooltip("Make this pack's rules (the ones that are on) one rule with all their classes and names");
        }
        string order = OrderNote(view, p, "");
        if (order.Length > 0) w.TextDisabled(order);
        w.Unindent(RowIndent);
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
        w.Selectable(Shown(SortRules.Describe(r)), k + ".row", selected, () =>
        {
            _selected = ReferenceEquals(_selected, r) ? null : r;
            _nameDraft = string.Empty;
        });
        w.Tooltip(selected ? "Click to close the editor" : "Click to change this rule");
        if (selected) DrawEditor(w, view, rules, r);
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
            rules.Remove(r);
            if (ReferenceEquals(_selected, r)) _selected = null;
            _save();
        });
        w.Unindent(EditorIndent);
    }

    private void AddDraft(SortRule r)
    {
        if (r.AddName(_nameDraft)) _save();
        _nameDraft = string.Empty;
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
        SortRule? keep = null;
        foreach (SortRule r in rules)
        {
            if (!packRules.Contains(r) || !r.On || r.IsEmpty) continue;
            if (keep == null) { keep = r; continue; }
            keep.TakeEntriesOf(r);
        }
        if (keep == null) return;
        rules.RemoveAll(r => !ReferenceEquals(r, keep) && packRules.Contains(r) && r.On && !r.IsEmpty);
        _selected = keep;
        _save();
    }

    /// <summary>Text as a label shows it: "##" would start ImGui's id part.</summary>
    private static string Shown(string text) => text.Replace("##", "# #");

    // ── Helpers (pure, for the tests) ───────────────────────────────────────

    /// <summary>The pack tooltip: how packs are numbered and that a rule keeps its pack.</summary>
    internal static string PackTooltip(SortView view) => view.PackOrderKnown ? PackTooltipTopDown : PackTooltipListOrder;

    internal const string PackTooltipTopDown =
        "Packs are numbered from the top of the pack column (foci don't count). "
        + "A rule remembers the pack you pick, so adding, moving or emptying packs doesn't change where items go.";

    internal const string PackTooltipListOrder =
        "A rule remembers the pack you pick, so adding, moving or emptying packs doesn't change where items go. "
        + "Pack numbers count your side packs (not foci) in the client's list order, oldest pack first, "
        + "which can differ from the pack column on screen: go by the name and what's in it.";

    /// <summary>" - in order", " - 3 to put in order", " - order not known": a pack's order as the window says it (empty when ordering is off).</summary>
    internal static string OrderNote(SortView view, PackLine p, string lead)
    {
        if (!view.Ordering) return string.Empty;
        if (!p.OrderKnown) return lead + "order not known yet: not put in order";
        if (p.OutOfPlace == 0) return lead + "in order";
        return lead + (p.OutOfPlace == 1 ? "1 item to put in order" : $"{p.OutOfPlace} items to put in order");
    }

    /// <summary>Rules by the side pack they send to now (1-based; 0 = a pack that isn't carried).</summary>
    internal static Dictionary<int, List<SortRule>> Group(IReadOnlyList<SortRule> rules, SortView view)
    {
        var map = new Dictionary<int, List<SortRule>>();
        foreach (SortRule r in rules)
        {
            int n = ShownPack(view, r);
            if (!map.TryGetValue(n, out List<SortRule>? list)) map[n] = list = new List<SortRule>();
            list.Add(r);
        }
        return map;
    }

    /// <summary>
    /// Classes and names that two rules (that are on) send to different packs, by the pack whose
    /// rule loses: "Armor: also in a rule for pack 2, which wins". A name beats a class, so only
    /// rules that match the same way compete; between those the rule saved first wins.
    /// </summary>
    internal static Dictionary<int, List<string>> Conflicts(IReadOnlyList<SortRule> rules, SortView view)
    {
        var map = new Dictionary<int, List<string>>();
        void Add(int pack, string line)
        {
            if (!map.TryGetValue(pack, out List<string>? l)) map[pack] = l = new List<string>();
            if (!l.Contains(line)) l.Add(line);
        }
        string Where(int pack) => pack > 0 ? "pack " + pack : "a pack you don't have";

        var classWinner = new Dictionary<int, int>();
        var nameWinner = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (SortRule r in rules)
        {
            if (!r.On) continue;
            int pack = ShownPack(view, r);
            foreach (int cls in r.Classes)
            {
                if (!classWinner.TryGetValue(cls, out int won)) classWinner[cls] = pack;
                else if (won != pack) Add(pack, $"{ObjectClasses.Label(cls)}: also in a rule for {Where(won)}, which wins");
            }
            foreach (string n in r.Names)
            {
                if (!nameWinner.TryGetValue(n, out int won)) nameWinner[n] = pack;
                else if (won != pack) Add(pack, $"\"{n}\": also in a rule for {Where(won)}, which wins");
            }
        }
        return map;
    }

    /// <summary>
    /// For side pack <paramref name="number"/>: rules that use it only because their own pack isn't
    /// carried (their number names it): "Gem, Key: its pack (Sack - Gems) isn't carried, so this one is used".
    /// </summary>
    internal static List<string> FallbackWarnings(SortView view, int number)
    {
        var lines = new List<string>();
        foreach (RulePackIssue issue in view.Issues)
        {
            if (issue.UsedPack != number) continue;
            string note = issue.Rule.PackNote;
            lines.Add($"{SortRules.Describe(issue.Rule)}: {(note.Length > 0 ? "its pack (" + note + ")" : "its pack")} isn't carried, so this one is used");
        }
        return lines;
    }

    /// <summary>Points <paramref name="rule"/> at side pack <paramref name="number"/> (1-based): that pack's id when it is carried.</summary>
    internal static void SetPack(SortRule rule, SortView view, int number)
    {
        rule.Pack = Math.Clamp(number, 1, SortStore.MaxPack);
        if (rule.Pack <= view.Packs.Count)
        {
            PackLine p = view.Packs[rule.Pack - 1];
            rule.PackId = p.Id;
            rule.PackNote = p.Note;
        }
        else
        {
            rule.PackId = 0;
            rule.PackNote = string.Empty;
        }
    }

    /// <summary>The side pack the rule uses now (1-based), as the planner resolves it, or 0.</summary>
    internal static int ShownPack(SortView view, SortRule r)
    {
        if (r.PackId != 0)
            for (int i = 0; i < view.Packs.Count; i++)
                if (view.Packs[i].Id == r.PackId) return i + 1;
        return r.Pack >= 1 && r.Pack <= view.Packs.Count ? r.Pack : 0;
    }

    /// <summary>"Pack 1: Sack (12/24) - Salvage, Gems" for each side pack, then "(none)" up to at least 7.</summary>
    internal static List<string> PackChoices(SortView view, int atLeast)
    {
        int n = Math.Min(Math.Max(Math.Max(view.Packs.Count, 7), atLeast), SortStore.MaxPack);
        var list = new List<string>(n);
        for (int i = 1; i <= n; i++)
        {
            if (i <= view.Packs.Count) list.Add($"Pack {i}: {view.Packs[i - 1].Label}");
            else list.Add($"Pack {i} (none)");
        }
        return list;
    }
}

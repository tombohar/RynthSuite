using System;
using System.Collections.Generic;
using RynthCore.Plugin.RynthSort.Sorting;
using RynthCore.Plugin.RynthSort.Ui;
using RynthCore.Plugin.Shared;

namespace RynthCore.Plugin.RynthSort.Views;

/// <summary>
/// The RynthSort window: the auto-sort box and status, the job buttons, then one collapsing
/// section per side pack ("Pack 1: Sack (12/24) - Components", top pack first) holding a line
/// that says which pack in the game it is (its icon, "Select in game", and its place in the
/// pack column), that pack's warnings and its rules (A-Z), a section for rules whose pack isn't
/// carried, and the settings. A rule is one row (on/off box, its classes and names); selecting it opens its
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
    /// <summary>The engine draws item icons in script windows (op level 2).</summary>
    public Func<bool> Icons = () => false;
    /// <summary>The engine can select objects (else no Select button).</summary>
    public Func<bool> CanSelect = () => false;
    /// <summary>Selects an object in the game (Host.SelectItem); false when it couldn't.</summary>
    public Func<uint, bool> SelectInGame = _ => false;

    private uint _selectedPack;
    private string _selectNote = string.Empty;

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
        w.Tooltip(PackHeaderTooltip(view, number));
        if (!open) return;

        w.Indent(RowIndent);
        DrawWhere(w, view, p, number);
        if (full) w.TextColored(UiColors.Amber, "This pack is full: items for it wait where they are");
        foreach (string line in warnings) w.TextColored(UiColors.Amber, line);
        packRules.Sort(SortRules.CompareForList);
        foreach (SortRule r in packRules) DrawRule(w, view, rules, r);
        if (packRules.Count == 0) w.TextDisabled("No rules send items here.");

        uint id = p.Id;
        w.SmallButton("+ Add rule", $"add.{id:X8}", () => AddRule(rules, view, number));
        w.Tooltip("A rule sends items to this pack: items of any of its classes, or with any of its names in their name. A name beats a class.");
        w.SameLine();
        w.SmallButton("Empty this pack", $"unpack.{id:X8}", () => _c.StartUnpack(number));
        w.Tooltip($"Move everything in pack {number} into the main pack. Turns auto-sort off first, so nothing moves back. Also: /rsort unpack {number}");
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
        w.TextDisabled(WhereText(view, p, number));
        if (_selectedPack == p.Id && _selectNote.Length > 0) w.TextDisabled(_selectNote);
    }

    private const float WhereIcon = 20f;

    internal const string SelectTooltip =
        "Selects this pack in the game, as clicking it in your inventory would, so the game shows you which one it is "
        + "(its name in the selection bar). Nothing moves.";

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
        "Pack 1 is the top pack in your inventory's pack column, pack 2 the one below it, and so on (foci don't count). "
        + "A rule remembers the pack itself, so moving, adding or emptying packs never sends items to the wrong one.";

    internal const string PackTooltipListOrder =
        "Right now the pack numbers here may not match your inventory's pack column (they go oldest pack first): "
        + "go by the pack's name, its item count and what's in it, or open the pack here and use Select in game. "
        + "A rule remembers the pack itself, so items still go to the right one.";

    /// <summary>The header's tooltip: which pack in the game this is, in plain words.</summary>
    internal static string PackHeaderTooltip(SortView view, int number)
    {
        PackLine p = view.Packs[number - 1];
        string what = $"Your {p.Name}: {p.Used} of {p.Capacity} items ({p.Summary}).";
        string where = view.PackOrderKnown
            ? $"It is the {Ordinal(number)} pack from the top of your inventory's pack column{SlotAside(p, number)}."
            : "Its place in your inventory's pack column isn't known right now, so its number may not match the column.";
        return what + " " + where + " Open it here and use Select in game to see it picked out in the game.\n\n" + PackTooltip(view);
    }

    /// <summary>The pack's first line: "2nd pack from the top of the pack column - 14/24 items", or that it isn't known.</summary>
    internal static string WhereText(SortView view, PackLine p, int number)
    {
        if (view.PackOrderKnown)
            return $"{Ordinal(number)} pack from the top of the pack column - {p.Used}/{p.Capacity} items";
        return $"Place in the pack column not known - {p.Used}/{p.Capacity} items";
    }

    /// <summary>
    /// " (the game's own inventory window also shows the focus above it, so there it is the 3rd entry)"
    /// when foci sit above it in the column: the retail window lists foci among the packs; RynthCore's
    /// Inventory panel lists them after the packs, so there the pack number is the row.
    /// </summary>
    private static string SlotAside(PackLine p, int number)
    {
        if (p.ColumnSlot <= number) return string.Empty;
        int foci = p.ColumnSlot - number;
        return $" (the game's own inventory window also shows the {(foci == 1 ? "focus" : foci + " foci")} above it, so there it is the {Ordinal(p.ColumnSlot)} entry)";
    }

    /// <summary>Above the packs when their numbers aren't the pack column's: why, in plain words; else empty.</summary>
    internal static string NumberingNote(SortView view)
    {
        if (view.Packs.Count == 0 || view.PackOrderKnown) return string.Empty;
        string why = view.OrderSource switch
        {
            PackOrderSource.NotSeen => "RynthCore hasn't seen your pack list since this character logged in (it was reloaded after the login). Log out and back in once to fix it.",
            PackOrderSource.Mismatch => "RynthCore's pack list is missing a pack you carry. Log out and back in once to fix it.",
            _ => "this RynthCore can't read the pack column's order (it needs plugin API 80).",
        };
        return "Pack numbers here go oldest pack first, not in your pack column's order: " + why
               + " Until then go by each pack's name and contents, or use Select in game.";
    }

    /// <summary>1st, 2nd, 3rd, 4th ... 11th, 12th, 13th ... 21st.</summary>
    internal static string Ordinal(int n)
    {
        int t = n % 100;
        string suffix = t is >= 11 and <= 13 ? "th" : (n % 10) switch { 1 => "st", 2 => "nd", 3 => "rd", _ => "th" };
        return n.ToString(System.Globalization.CultureInfo.InvariantCulture) + suffix;
    }

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

using System;
using System.Collections.Generic;
using RynthCore.Plugin.RynthSort.Sorting;
using RynthCore.Plugin.RynthSort.Ui;
using RynthCore.Plugin.Shared;

namespace RynthCore.Plugin.RynthSort.Views;

/// <summary>The window's four pages.</summary>
internal enum PanelTab { Packs, Moves, Layouts, Settings }

/// <summary>
/// The RynthSort window (redesigned 2026-11-26 after #rynth-issues "The bag sort feature is lacking
/// in UX"). Always on top: the Auto-sort box with one coloured line that says what it is doing
/// (off, on and tidy, moving, paused and why), a line or two on what waits and what is left alone,
/// the Sort now / Clean packs / Put back buttons and any warning. Below, four pages:
/// <list type="bullet">
/// <item>Packs (SortPanel.Packs.cs): "what goes in this pack", one section per side pack, top pack
/// first: which pack in the game it is (icon, Select in game, its place in the column), a strip of
/// what is in it, its rules with how many items they hold and bring, a kind picker, + Add rule,
/// + Add selected, Empty this pack, Merge rules; rules for packs not carried; an Undo for the last
/// big rule change.</item>
/// <item>Moves (SortPanel.Moves.cs): what a sort would move now (a table at op level 3), what waits,
/// the items left alone because the player used them (and "Sort them too"), the last moves and Put back.</item>
/// <item>Layouts (SortPanel.Layouts.cs): the starter layouts, fitted to the packs carried and
/// previewed before anything changes.</item>
/// <item>Settings (SortPanel.Settings.cs): when to sort, how long to leave used items alone, order
/// inside packs, Empty side packs.</item>
/// </list>
/// It works at script-window op level 2 (released engines); with op level 3 the status line is bold
/// and the move lists are tables. Recorded on the pump thread, replayed by the engine.
/// </summary>
internal sealed partial class SortPanel
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
    /// <summary>The engine's script-window op level (UiGetInfo; 0 when unknown). 3 adds tables and fonts.</summary>
    public Func<int> OpLevel = () => 0;
    /// <summary>The engine can select objects (else no Select button).</summary>
    public Func<bool> CanSelect = () => false;
    /// <summary>Selects an object in the game (Host.SelectItem); false when it couldn't.</summary>
    public Func<uint, bool> SelectInGame = _ => false;
    /// <summary>The item selected in the game, read against the packs now (the plugin's host read; "unavailable" by default).</summary>
    public Func<SelectedItem> ReadSelected = () => SelectedItem.Of(SelectionKind.Unavailable);
    /// <summary>The clock for notes that fade (the tests set it).</summary>
    public Func<long> Now = () => Environment.TickCount64;

    private uint _selectedPack;
    private string _selectNote = string.Empty;
    private const long NoteMs = 60_000;
    private uint _notePack;
    private string _note = string.Empty;
    private bool _noteGood;
    private long _noteUntilMs;

    private PanelTab _tab = PanelTab.Packs;
    private bool _tabChosen;
    private string _forCharacter = string.Empty;
    /// <summary>This pass's preview of a sort (from the last read), or null before the first read.</summary>
    private SortPreview? _preview;

    public SortPanel(SortController c, Action save)
    {
        _c = c;
        _save = save;
    }

    /// <summary>The rule whose editor is open, or null.</summary>
    public SortRule? Selected => _selected;
    /// <summary>The page shown.</summary>
    public PanelTab Tab => _tab;

    private bool Rich => OpLevel() >= UiFormat.OpLevelRich;

    public void Draw(UiWindow w)
    {
        string who = Character();
        if (who.Length == 0)
        {
            w.TextDisabled("Log in to sort your packs.");
            return;
        }
        if (!string.Equals(who, _forCharacter, StringComparison.Ordinal)) ForgetCharacter(who);
        List<SortRule> rules = _c.Data.Rules;
        if (_selected != null && !rules.Contains(_selected)) _selected = null;
        if (!_tabChosen)
        {
            // A character with no rules yet starts on the layouts: the quickest way to a sensible setup.
            _tab = rules.Count == 0 ? PanelTab.Layouts : PanelTab.Packs;
            _tabChosen = true;
        }
        SortView view = _c.Planner.View;
        SortInventory? inv = _c.LastInventory;
        _preview = inv != null ? SortPreview.Build(inv, rules, _c.CurrentOptions, _c.IsHeld) : null;

        DrawStatus(w, view, rules);
        DrawTabs(w);
        switch (_tab)
        {
            case PanelTab.Moves: DrawMovesTab(w, view); break;
            case PanelTab.Layouts: DrawLayoutsTab(w, rules); break;
            case PanelTab.Settings: DrawSettingsTab(w); break;
            default: DrawPacksTab(w, view, rules); break;
        }
    }

    /// <summary>Another character: the window starts fresh (its page, picks and undo are the last one's).</summary>
    private void ForgetCharacter(string who)
    {
        _forCharacter = who;
        _tabChosen = false;
        _selected = null;
        _nameDraft = string.Empty;
        _layoutKey = null;
        _layoutNote = string.Empty;
        _undo = null;
        _undoWhat = string.Empty;
        _note = _selectNote = string.Empty;
    }

    /// <summary>Shows a page ("Pick a layout" and the like); the next pass draws it.</summary>
    internal void ShowTab(PanelTab tab)
    {
        _tab = tab;
        _tabChosen = true;
    }

    // ── Always on top ───────────────────────────────────────────────────────

    private void DrawStatus(UiWindow w, SortView view, List<SortRule> rules)
    {
        SortSettings s = _c.Data.Settings;
        w.Checkbox("Auto-sort", "auto", s.Auto, v => _c.SetAuto(v));
        w.Tooltip(AutoTooltip);
        w.SameLine();
        var (text, color) = Headline(_c, view, rules.Count == 0);
        // Wrapped beside the box (a narrow window wraps it in a column of its own, never off the edge).
        if (Rich) w.PushFont(UiFormat.FontBold);
        w.PushStyleColor(UiFormat.ColText, color);
        w.TextWrapped(text);
        w.PopStyleColor();
        if (Rich) w.PopFont();
        w.Tooltip(HeadlineTooltip);

        List<string> details = Details(_c, view, _preview, rules.Count == 0);
        if (details.Count > 0)
        {
            w.PushStyleColor(UiFormat.ColText, UiColors.Soft);
            foreach (string line in details) w.TextWrapped(line);
            w.PopStyleColor();
        }

        w.Button("Sort now", "job.all", () => _c.StartJob(SortScope.All));
        w.Tooltip("Sort every pack once, now: items go to their packs, stacks are joined and packs put in order. "
                  + "It doesn't wait for new items to settle; items you used lately are still left alone.");
        w.SameLine();
        w.Button("Clean packs", "job.clean", () => _c.StartJob(SortScope.Clean));
        w.Tooltip("Sort now, and side-pack items no rule wants go back to the main pack");
        int back = _c.PutBackCount;
        if (back > 0)
        {
            w.SameLine();
            w.Button("Put back", "job.back", () => _c.StartPutBack());
            w.Tooltip($"Undo the last sort: the {Count(back, "item")} it moved go back where they were. "
                      + "Turns auto-sort off first, so nothing moves again. Also: /rsort back");
        }

        if (view.MainFull) w.TextColored(UiColors.Amber, "The main pack is full");
        if (s.Auto && _c.LastBot is RynthAiActivity bot && (bot & RynthAiActivity.AutoCram) != 0 && (bot & RynthAiActivity.MacroRunning) != 0)
            w.TextColored(UiColors.Amber, "RynthAi's AutoCram also moves loot out of the main pack; turn one of them off");
        foreach (string warning in Warnings()) w.TextColored(UiColors.Amber, warning);
    }

    private void DrawTabs(UiWindow w)
    {
        TabButton(w, "Packs", "tab.packs", PanelTab.Packs, "What goes in each pack: its rules, what is in it, and where it is in the game");
        w.SameLine();
        int moves = _preview?.Moves.Count ?? 0;
        TabButton(w, moves > 0 ? $"Moves ({moves})" : "Moves", "tab.moves", PanelTab.Moves,
            "What a sort would move now, the items left alone because you used them, and the last moves");
        w.SameLine();
        TabButton(w, "Layouts", "tab.layouts", PanelTab.Layouts, "Ready-made rules for typical packs: see where things would go, then use one in a click");
        w.SameLine();
        TabButton(w, "Settings", "tab.settings", PanelTab.Settings, "When to sort, how long to leave items you use alone, order inside packs");
        w.Separator();
    }

    private void TabButton(UiWindow w, string label, string id, PanelTab tab, string tip)
    {
        w.ToggleButton(label, id, _tab == tab, () => ShowTab(tab));
        w.Tooltip(tip);
    }

    internal const string AutoTooltip =
        "On: RynthSort keeps your packs tidy by itself. New items go to their pack once they have sat in the main pack for the settle time, "
        + "items in the wrong pack move to theirs, stacks are joined and items are put in order. "
        + "It waits while you loot, trade, use a vendor or cast, and leaves items you used lately alone. Off: nothing moves unless you press Sort now.";

    internal const string HeadlineTooltip =
        "What RynthSort is doing now. Green: all is well. Blue: moving items. Amber: something is in the way (see the line below). Grey: off.";

    // ── Status words (pure, for the tests) ──────────────────────────────────

    /// <summary>The coloured line beside the Auto-sort box: what it is doing, in a few words.</summary>
    internal static (string Text, uint Color) Headline(SortController c, SortView v, bool noRules)
    {
        string status = c.Status;
        if (c.Paused) return ("Paused: " + PausedWords(status["paused: ".Length..]), UiColors.Amber);
        if (c.Job is SortScope job)
            return (job switch
            {
                SortScope.Unpack => "Emptying side packs",
                SortScope.PutBack => "Putting items back",
                SortScope.Clean => "Cleaning packs",
                _ => "Sorting now",
            }, UiColors.Blue);
        if (!c.Data.Settings.Auto) return ("Off: nothing moves by itself", UiColors.Grey);
        if (status == "sorting") return ("On: moving items", UiColors.Blue);
        if (status is "starting" or "reading your packs" or "waiting for the inventory") return ("On: reading your packs", UiColors.Grey);
        int stuck = v.WaitFull + v.WaitNoPack + v.WaitMainFull;
        if (stuck > 0) return ($"On: {Count(stuck, "item")} can't move", UiColors.Amber);
        if (v.WaitSettle > 0)
            return ($"On: {Count(v.WaitSettle, "new item")} {(v.WaitSettle == 1 ? "moves" : "move")} in {Seconds(v.NextSettleMs)}", UiColors.Green);
        if (noRules) return ("On: no rules yet", UiColors.Amber);
        return ("On: all tidy", UiColors.Green);
    }

    /// <summary>The lines under it: what waits and why, what is left alone, what moved last.</summary>
    internal static List<string> Details(SortController c, SortView v, SortPreview? p, bool noRules)
    {
        var lines = new List<string>();
        if (noRules)
            lines.Add("No rules yet, so nothing goes to your side packs. Pick a starter layout, or add rules to each pack.");
        else if (!c.Data.Settings.Auto && c.Job == null && p != null && !p.Empty)
            lines.Add($"{Count(p.Moves.Count, "item")} would move when you sort (see Moves).");

        var wait = new List<string>();
        if (v.WaitFull > 0) wait.Add($"{Count(v.WaitFull, "item")} {(v.WaitFull == 1 ? "waits" : "wait")} for a full pack");
        if (v.WaitNoPack > 0) wait.Add($"{Count(v.WaitNoPack, "item")} for a pack you don't carry");
        if (v.WaitMainFull > 0) wait.Add($"{Count(v.WaitMainFull, "item")} for room in the main pack");
        if (wait.Count > 0) lines.Add(Capital(string.Join("; ", wait)) + ".");

        int held = p?.View.Held.Count ?? v.Held.Count;
        if (held > 0) lines.Add($"{Count(held, "item")} left alone because you used {(held == 1 ? "it" : "them")} lately (see Moves).");
        if (c.Log.Count > 0) lines.Add("Last: " + c.Log[0]);
        return lines;
    }

    /// <summary>Why it is paused, for a player: "a corpse or chest is open" for "looting".</summary>
    internal static string PausedWords(string reason) => reason switch
    {
        "portal space" => "in portal space",
        "looting" => "a corpse or chest is open",
        "vendor open" => "a vendor is open",
        "trading" => "a trade is open",
        "busy" => "you are casting or using something",
        _ => reason,
    };

    /// <summary>"1 item", "3 items".</summary>
    internal static string Count(int n, string what) => n == 1 ? "1 " + what : $"{n} {what}s";

    /// <summary>"3 s", or "a moment" under a second.</summary>
    internal static string Seconds(long ms) => ms < 1000 ? "a moment" : $"{(ms + 999) / 1000} s";

    private static string Capital(string s) => s.Length > 0 ? char.ToUpperInvariant(s[0]) + s[1..] : s;

    /// <summary>"main pack", "pack 3".</summary>
    internal static string PackWord(int index) => index == 0 ? "main pack" : "pack " + index;

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

    internal const string SelectTooltip =
        "Selects this pack in the game, as clicking it in your inventory would, so the game shows you which one it is "
        + "(its name in the selection bar). Nothing moves.";

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

    /// <summary>
    /// The kinds of item in a pack for its icon strip: one entry per kind (same class id, else the
    /// same name), the commonest first (then A-Z), at most <paramref name="most"/>.
    /// </summary>
    internal static List<(SortItem First, int Count)> Kinds(SortPack pack, int most)
    {
        var kinds = new List<(SortItem First, int Count)>();
        foreach (SortItem it in pack.Items)
        {
            int at = kinds.FindIndex(k => SortPlanner.SameKind(k.First, it));
            if (at >= 0) kinds[at] = (kinds[at].First, kinds[at].Count + Math.Max(1, it.Stack));
            else kinds.Add((it, Math.Max(1, it.Stack)));
        }
        kinds.Sort((a, b) => a.Count != b.Count ? b.Count.CompareTo(a.Count) : string.Compare(a.First.Name, b.First.Name, StringComparison.OrdinalIgnoreCase));
        if (kinds.Count > most) kinds.RemoveRange(most, kinds.Count - most);
        return kinds;
    }

    /// <summary>For each rule: how many items it keeps in its pack now, and how many a sort would bring in.</summary>
    internal static Dictionary<SortRule, (int Here, int Coming)> RuleCounts(IReadOnlyList<SortRule> rules, SortView view, SortInventory? inv, SortPreview? preview)
    {
        var map = new Dictionary<SortRule, (int Here, int Coming)>(ReferenceEqualityComparer.Instance);
        if (inv == null) return map;
        for (int i = 0; i < inv.Packs.Count; i++)
            foreach (SortItem it in inv.Packs[i].Items)
            {
                SortRule? r = SortRules.RuleFor(rules, it);
                if (r == null || ShownPack(view, r) != i + 1) continue;
                map.TryGetValue(r, out var c);
                map[r] = (c.Here + 1, c.Coming);
            }
        if (preview != null)
            foreach (SortMove m in preview.Moves)
            {
                SortRule? r = SortRules.RuleFor(rules, m.Item);
                if (r == null || m.DestIndex == 0 || ShownPack(view, r) != m.DestIndex) continue;
                map.TryGetValue(r, out var c);
                map[r] = (c.Here, c.Coming + 1);
            }
        return map;
    }
}

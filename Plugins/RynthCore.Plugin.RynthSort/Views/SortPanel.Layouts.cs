using System;
using System.Collections.Generic;
using RynthCore.Plugin.RynthSort.Sorting;
using RynthCore.Plugin.RynthSort.Ui;

namespace RynthCore.Plugin.RynthSort.Views;

// The Layouts page: starter layouts fitted to the packs carried, previewed before anything changes.
internal sealed partial class SortPanel
{
    private string? _layoutKey;
    private string _layoutNote = string.Empty;
    private bool _layoutNoteGood;
    private long _layoutNoteUntilMs;

    /// <summary>The layout picked to look at (not used yet), or null.</summary>
    public string? PickedLayout => _layoutKey;

    private void DrawLayoutsTab(UiWindow w, List<SortRule> rules)
    {
        DrawLayoutNote(w);
        w.PushStyleColor(UiFormat.ColText, UiColors.Soft);
        w.TextWrapped("A layout gives each kind of item a side pack of its own, choosing the pack that already holds most of it. "
                      + "Pick one to see where things would go. Nothing changes until you press Use this layout, and nothing moves until you sort.");
        w.PopStyleColor();

        SortInventory? inv = _c.LastInventory;
        if (inv == null)
        {
            w.TextDisabled("Reading your packs...");
            return;
        }
        if (inv.Packs.Count == 0)
        {
            w.TextColored(UiColors.Amber, "You carry no side packs, so a layout has nowhere to put things. Get a pack and come back.");
            return;
        }

        foreach (StarterLayout l in StarterLayouts.All)
        {
            bool picked = string.Equals(_layoutKey, l.Key, StringComparison.Ordinal);
            string key = l.Key;
            w.Selectable(l == StarterLayouts.All[0] ? l.Name + "  (a good start)" : l.Name, "layout." + l.Key, picked,
                () => _layoutKey = picked ? null : key);
            w.Tooltip(picked ? "Click to close the preview" : "Click to see where things would go");
            w.Indent(EditorIndent);
            w.PushStyleColor(UiFormat.ColText, UiColors.Grey);
            w.TextWrapped(l.Blurb);
            w.PopStyleColor();
            w.Unindent(EditorIndent);
            if (picked) DrawLayoutPreview(w, l, inv, rules);
        }
    }

    private void DrawLayoutPreview(UiWindow w, StarterLayout l, SortInventory inv, List<SortRule> rules)
    {
        LayoutPlan plan = StarterLayouts.Fit(l, inv);
        SortPreview p = SortPreview.Build(inv, plan.Rules, _c.CurrentOptions, _c.IsHeld);
        w.Indent(EditorIndent);
        w.SeparatorText($"{l.Name} on your {Count(inv.Packs.Count, "pack")}");
        if (Rich)
        {
            w.BeginTable("layout." + l.Key + ".table", 3, UiFormat.TableRowBg | UiFormat.TableBordersInnerH | UiFormat.TableSizingStretchProp);
            w.TableSetupColumn("Pack", UiFormat.ColumnWidthStretch, 1.6f);
            w.TableSetupColumn("Gets", UiFormat.ColumnWidthStretch, 2.6f);
            w.TableSetupColumn("Moves in", UiFormat.ColumnWidthStretch, 1f);
            w.TableHeadersRow();
            foreach (var (group, pack) in plan.Placed)
            {
                w.TableNextRow();
                w.TableNextColumn();
                PackCell(w, inv, pack);
                w.TableNextColumn();
                w.Text(group.Label);
                w.TableNextColumn();
                w.Text(p.Into[pack].ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
            w.EndTable();
        }
        else
        {
            foreach (var (group, pack) in plan.Placed)
            {
                PackCell(w, inv, pack);
                w.SameLine();
                w.Text(group.Label);
                w.SameLine();
                w.TextDisabled(p.Into[pack] == 0 ? "- nothing to move in" : $"- {p.Into[pack]} to move in");
            }
        }
        foreach (int spare in plan.Spare)
            w.TextDisabled($"Pack {spare} ({inv.Packs[spare - 1].Name}) stays free for your own rules.");
        if (plan.Left.Count > 0)
        {
            var left = plan.Left.ConvertAll(g => g.Label.ToLowerInvariant());
            w.PushStyleColor(UiFormat.ColText, UiColors.Grey);
            w.TextWrapped($"No pack left for {string.Join("; ", left)}: they stay in the main pack.");
            w.PopStyleColor();
        }

        string moved = p.Moves.Count == 0 ? "Nothing would move" : $"{Count(p.Moves.Count, "item")} would move";
        w.Text(plan.AlreadyThere > 0 ? $"{moved}; {plan.AlreadyThere} already in place." : moved + ".");
        if (p.View.Full.Count > 0) w.TextColored(UiColors.Amber, "Some packs would be full: those items wait in the main pack.");
        if (rules.Count > 0)
            w.TextColored(UiColors.Amber, $"This replaces your {Count(rules.Count, "rule")} (Undo brings them back).");

        w.Button("Use this layout", "layout.use", () => UseLayout(l));
        w.Tooltip("Set these rules. Nothing moves: press Sort now afterwards, or turn on Auto-sort.");
        w.SameLine();
        w.Button("Cancel", "layout.cancel", () => _layoutKey = null);
        w.Unindent(EditorIndent);
    }

    private void PackCell(UiWindow w, SortInventory inv, int pack)
    {
        SortPack sp = inv.Packs[pack - 1];
        if (Icons())
        {
            w.Image(UiFormat.IconKindObject, sp.Id, RowIcon, RowIcon);
            w.SameLine();
        }
        w.Text($"Pack {pack}");
        w.Tooltip($"{sp.Name}: {sp.Items.Count} of {SortPlanner.CapacityOf(sp, SortPlanner.SideCapacityFallback)} items ({sp.Summary()})");
    }

    /// <summary>The layout's rules replace the character's (Undo keeps the old ones); nothing moves.</summary>
    internal void UseLayout(StarterLayout l)
    {
        SortInventory? inv = _c.LastInventory;
        if (inv == null || inv.Packs.Count == 0) return;
        List<SortRule> rules = _c.Data.Rules;
        LayoutPlan plan = StarterLayouts.Fit(l, inv);
        var before = Snapshot(rules);
        rules.Clear();
        rules.AddRange(plan.Rules);
        KeepUndo(before, "the " + l.Name + " layout");
        _selected = null;
        _layoutKey = null;
        _layoutNote = $"Rules set from {l.Name}: {Count(plan.Rules.Count, "pack")} {(plan.Rules.Count == 1 ? "has" : "have")} a kind of item. "
                      + "Nothing has moved yet: press Sort now, or turn on Auto-sort.";
        _layoutNoteGood = true;
        _layoutNoteUntilMs = Now() + 3 * NoteMs;
        ShowTab(PanelTab.Packs);
        _save();
    }

    private void DrawLayoutNote(UiWindow w)
    {
        if (_layoutNote.Length == 0 || Now() >= _layoutNoteUntilMs) return;
        w.PushStyleColor(UiFormat.ColText, _layoutNoteGood ? UiColors.Green : UiColors.Amber);
        w.TextWrapped(_layoutNote);
        w.PopStyleColor();
    }
}

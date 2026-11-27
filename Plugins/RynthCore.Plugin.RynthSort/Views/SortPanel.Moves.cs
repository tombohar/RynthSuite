using System;
using System.Collections.Generic;
using RynthCore.Plugin.RynthSort.Sorting;
using RynthCore.Plugin.RynthSort.Ui;

namespace RynthCore.Plugin.RynthSort.Views;

// The Moves page: what a sort would move now, what is left alone, what moved last.
internal sealed partial class SortPanel
{
    private const float RowIcon = 16f;
    /// <summary>The most moves listed (the rest are counted).</summary>
    internal const int MoveRows = 30;

    private void DrawMovesTab(UiWindow w, SortView view)
    {
        SortSettings s = _c.Data.Settings;
        w.SeparatorText("Next moves");
        SortPreview? p = _preview;
        if (p == null)
        {
            w.TextDisabled("Reading your packs...");
        }
        else if (p.Empty)
        {
            w.TextColored(UiColors.Green, "Nothing to move: every item is in its pack.");
            if (p.ToOrder > 0) w.TextDisabled($"{Count(p.ToOrder, "item")} still to put in order inside packs.");
        }
        else
        {
            w.Text(s.Auto || _c.Job != null
                ? $"{Count(p.Moves.Count, "item")} {(p.Moves.Count == 1 ? "goes" : "go")} to {(p.Moves.Count == 1 ? "its pack" : "their packs")}:"
                : $"A sort would move {Count(p.Moves.Count, "item")}:");
            if (Rich) DrawMoveTable(w, p);
            else DrawMoveLines(w, p);
            if (p.Moves.Count > MoveRows) w.TextDisabled($"... and {p.Moves.Count - MoveRows} more.");
            if (p.ToOrder > 0) w.TextDisabled("Then stacks are joined and items put in order inside each pack.");
            if (!s.Auto && _c.Job == null)
            {
                w.PushStyleColor(UiFormat.ColText, UiColors.Soft);
                w.TextWrapped("Nothing moves until you press Sort now or turn on Auto-sort.");
                w.PopStyleColor();
            }
        }
        if (p != null) DrawWaiting(w, p.View);

        if (p != null && p.View.Held.Count > 0) DrawHeld(w, p.View.Held);

        w.SeparatorText("Done lately");
        if (_c.Log.Count == 0) w.TextDisabled("Nothing moved yet.");
        foreach (string line in _c.Log) w.TextDisabled(line);
        int back = _c.PutBackCount;
        if (back > 0)
        {
            w.SmallButton($"Put back {Count(back, "item")}", "moves.back", () => _c.StartPutBack());
            w.Tooltip("Undo the last sort: those items go back where they were. Turns auto-sort off first, so nothing moves again.");
        }
    }

    private void DrawMoveLines(UiWindow w, SortPreview p)
    {
        int n = Math.Min(p.Moves.Count, MoveRows);
        for (int i = 0; i < n; i++)
        {
            SortMove m = p.Moves[i];
            if (Icons())
            {
                w.Image(UiFormat.IconKindObject, m.Item.Id, RowIcon, RowIcon);
                w.SameLine();
            }
            w.Text(ItemName(m.Item));
            w.SameLine();
            w.TextDisabled($"{PackWord(Math.Max(m.FromIndex, 0))} -> {PackWord(m.DestIndex)}");
            w.Tooltip(MoveTooltip(m));
        }
    }

    /// <summary>The same list as a table (op level 3): Item, From, To.</summary>
    private void DrawMoveTable(UiWindow w, SortPreview p)
    {
        w.BeginTable("moves", 3, UiFormat.TableRowBg | UiFormat.TableBordersInnerH | UiFormat.TableSizingStretchProp);
        w.TableSetupColumn("Item", UiFormat.ColumnWidthStretch, 3f);
        w.TableSetupColumn("From", UiFormat.ColumnWidthStretch, 1.4f);
        w.TableSetupColumn("To", UiFormat.ColumnWidthStretch, 1.4f);
        w.TableHeadersRow();
        int n = Math.Min(p.Moves.Count, MoveRows);
        for (int i = 0; i < n; i++)
        {
            SortMove m = p.Moves[i];
            w.TableNextRow();
            w.TableNextColumn();
            if (Icons())
            {
                w.Image(UiFormat.IconKindObject, m.Item.Id, RowIcon, RowIcon);
                w.SameLine();
            }
            w.Text(ItemName(m.Item));
            w.Tooltip(MoveTooltip(m));
            w.TableNextColumn();
            w.TextDisabled(PackWord(Math.Max(m.FromIndex, 0)));
            w.TableNextColumn();
            w.Text(PackWord(m.DestIndex));
        }
        w.EndTable();
    }

    private string MoveTooltip(in SortMove m)
    {
        SortRule? r = SortRules.RuleFor(_c.Data.Rules, m.Item);
        string why = r != null ? $"Its rule: {SortRules.Describe(r)}." : "No rule wants it in a side pack (Clean sends it to the main pack).";
        string dest = m.DestIndex == 0 ? "the main pack" : $"pack {m.DestIndex} ({m.Dest.Name})";
        return $"{ItemName(m.Item)} goes to {dest}. {why}";
    }

    private static void DrawWaiting(UiWindow w, SortView v)
    {
        if (v.WaitSettle > 0)
            w.TextDisabled($"{Count(v.WaitSettle, "new item")} {(v.WaitSettle == 1 ? "waits" : "wait")} out the settle time in the main pack.");
        foreach (int pack in v.Full)
            w.TextColored(UiColors.Amber, $"Pack {pack} is full: items for it wait where they are.");
        if (v.WaitNoPack > 0)
            w.TextColored(UiColors.Amber, $"{Count(v.WaitNoPack, "item")} {(v.WaitNoPack == 1 ? "has a rule" : "have rules")} for a pack you don't carry (see Packs).");
        if (v.WaitMainFull > 0)
            w.TextColored(UiColors.Amber, "The main pack is full: Clean can't bring everything back.");
    }

    private void DrawHeld(UiWindow w, List<HeldLine> held)
    {
        w.SeparatorText("Left alone");
        w.PushStyleColor(UiFormat.ColText, UiColors.Soft);
        w.TextWrapped($"You used these lately, so they stay where they are for {Count(_c.Data.Settings.HoldMin, "minute")} (Settings).");
        w.PopStyleColor();
        int n = Math.Min(held.Count, MoveRows);
        long now = _c.Clock;
        for (int i = 0; i < n; i++)
        {
            HeldLine h = held[i];
            if (Icons())
            {
                w.Image(UiFormat.IconKindObject, h.Item.Id, RowIcon, RowIcon);
                w.SameLine();
            }
            w.Text(ItemName(h.Item));
            w.SameLine();
            string how = _c.Touch.TryGetHeld(h.Item.Id, now, _c.HoldMs, out long at, out TouchKind kind)
                ? $"{TouchWatch.Words(kind)} {Ago(now - at)}" : "you used it";
            w.TextDisabled($"- {how}; would go to {(h.To == 0 ? "the main pack" : "pack " + h.To)}");
        }
        if (held.Count > n) w.TextDisabled($"... and {held.Count - n} more.");
        w.SmallButton("Sort them too", "hold.release", () => _c.Touch.ReleaseAll());
        w.Tooltip("Stop leaving these items alone: the next sort moves them like any other. Items you use after this are left alone again.");
    }

    private static string ItemName(SortItem it) => it.Name.Length > 0 ? Shown(it.Name) : $"(item 0x{it.Id:X8})";

    /// <summary>"just now", "2 min ago".</summary>
    internal static string Ago(long ms) => ms < 60_000 ? "just now" : $"{ms / 60_000} min ago";
}

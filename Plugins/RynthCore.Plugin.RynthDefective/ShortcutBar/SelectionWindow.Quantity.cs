// ============================================================================
//  RynthDefective - ShortcutBar/SelectionWindow.Quantity.cs
//  The quantity box under the selection (2026-10-30, Quantity/QuantityController):
//  for a stack, a number with 1 / 10 / 100 / half / max, Split N into a pack
//  and Give N to the last player or NPC selected; for an item in a vendor's
//  list, Buy N with its price and what you have. Retail AC had this number in
//  its toolbar (gone with "Hide retail toolbar"); this one is RynthDefective's.
//
//    line 1   the number (typed: the game gets no keys while it has the
//             keyboard) and the quick numbers
//    line 2   Split N | into <pack> | Give N      or      Buy N | the price
//    line 3   what the last action did, or what the buttons will do
//
//  Render thread: draws the view the tick built. The number and the pack are
//  set at once (QuantityController.Value / Into); the actions go to the tick.
// ============================================================================

using System;
using System.Numerics;
using ImGuiNET;
using RynthCore.Plugin.RynthDefective.Model;
using RynthCore.Plugin.RynthDefective.Quantity;
using RynthCore.Plugin.Shared;
using RynthCore.Plugin.Shared.RynthUi;
using RynthCore.PluginSdk;
using static RynthCore.Plugin.Shared.RynthUi.RynthUi;

namespace RynthCore.Plugin.RynthDefective.ShortcutBar;

/// <summary>A click on one of the box's buttons.</summary>
internal enum QtyClick { Split, Give, Buy }

internal sealed partial class SelectionWindow
{
    /// <summary>The box's number and pack (set from here) and its last answer; null = no box.</summary>
    public QuantityController? Quantity { get; set; }

    /// <summary>A Split / Give / Buy click, with the count on the button (queued to the tick).</summary>
    public Action<QtyClick, int>? QuantityAction { get; set; }

    private const float QRow = 18, QGap = 4, QField = 54, QChipGap = 3;
    private const string IntoPopupId = "##qty_into";
    private static readonly uint OkColor = Argb(0xFF73D885), BadColor = Argb(0xFFE0564E), WarnColor = Argb(0xFFE8B04A);

    /// <summary>The box's height under the selection (pixels); 0 = none.</summary>
    private float QuantityHeight(QuantityView? q) =>
        q == null || Quantity == null ? 0f
        : MathF.Ceiling(Px(QGap) + Px(QRow) + Px(QGap) + Px(QRow) + Px(QGap) + Fs(Font(RynthUiFont.Narrow9)) + Px(Pad));

    private void DrawQuantity(ImDrawListPtr dl, QuantityView q, Vector2 top, float width)
    {
        QuantityController ctl = Quantity!;
        float pad = Px(Pad), rowH = Px(QRow), gap = Px(QGap);
        float left = top.X + pad, right = top.X + width - pad;
        dl.AddLine(new Vector2(left, top.Y), new Vector2(right, top.Y), A(_line, 0.8f));
        QtySubject s = q.Subject;
        int value = ctl.Value;
        float y = top.Y + gap;

        // Line 1: the number and the quick numbers.
        float fieldW = Math.Min(Px(QField), Math.Max(Px(30), right - left));
        NumberField(ctl, new Vector2(left, y), fieldW, rowH, s);
        float x = left + fieldW + Px(QChipGap + 2);
        int max = s.BoxMax;
        string none = s.Mode == QtyMode.Vendor ? s.Quote?.Refusal(1) ?? "You can't buy any now." : "Nothing to count.";
        if (Chip(dl, "##qty_1", "1", ref x, y, rowH, right, true, "One.")) ctl.Value = 1;
        if (Chip(dl, "##qty_10", "10", ref x, y, rowH, right, true, "Ten.")) ctl.Value = 10;
        if (Chip(dl, "##qty_100", "100", ref x, y, rowH, right, true, "A hundred.")) ctl.Value = 100;
        string halfTip = max < 1 ? none
            : s.Mode == QtyMode.Vendor ? $"Half of the most you can buy now ({QuantityRules.N(QuantityRules.HalfOf(max))})."
            : $"Half the stack ({QuantityRules.N(QuantityRules.HalfOf(max))}).";
        if (Chip(dl, "##qty_half", "half", ref x, y, rowH, right, max >= 1, halfTip)) ctl.Value = QuantityRules.ForBox(QtyAmount.Half, max);
        string maxTip = max < 1 ? none
            : s.Mode == QtyMode.Vendor && s.Quote is { BankUnknown: true } bq
                ? $"The most your packs and burden take of what the vendor has ({QuantityRules.N(max)}): {bq.MaxNote}; the server decides about the money."
            : s.Mode == QtyMode.Vendor && s.Quote is { BankKnown: true }
                ? $"The most you can buy now ({QuantityRules.N(max)}): what the vendor has, your pack and the bank pay for, and your packs take."
            : s.Mode == QtyMode.Vendor ? $"The most you can buy now ({QuantityRules.N(max)}): what the vendor has, you can pay for and your packs take."
            : $"The whole stack ({QuantityRules.N(max)}). A split still leaves one.";
        if (Chip(dl, "##qty_max", "max", ref x, y, rowH, right, max >= 1, maxTip)) ctl.Value = QuantityRules.ForBox(QtyAmount.Max, max);
        ImFontPtr sf = Font(RynthUiFont.Narrow9);
        string of = s.Mode == QtyMode.Stack ? "of " + QuantityRules.N(s.Stack) : max >= 1 ? "up to " + QuantityRules.N(max) : string.Empty;
        float ofW = of.Length > 0 ? sf.CalcTextSizeA(Fs(sf), float.MaxValue, 0f, of).X : 0f;
        if (of.Length > 0 && x + ofW <= right + 0.5f)
            dl.AddText(sf, Fs(sf), new Vector2(MathF.Round(right - ofW), MathF.Round(y + (rowH - Fs(sf)) * 0.5f)), _faint, of);

        // Line 2: what the number does.
        y += rowH + gap;
        x = left;
        if (s.Mode == QtyMode.Stack) StackButtons(dl, q, ctl, value, ref x, y, rowH, right);
        else VendorButtons(dl, q, value, ref x, y, rowH, right);

        // Line 3: the last answer, or what the buttons do.
        y += rowH + gap;
        QtyStatus? st = ctl.StatusAt(Environment.TickCount64);
        string line;
        uint color;
        if (st != null) { line = st.Text; color = st.Ok ? OkColor : BadColor; }
        else if (q.Busy) { line = "Waiting for the server..."; color = _dim; }
        else Hint(q, value, out line, out color);
        if (line.Length > 0)
        {
            ImGui.SetCursorScreenPos(new Vector2(left, y));
            ImGui.InvisibleButton("##qty_line", new Vector2(Math.Max(1, right - left), Math.Max(1, Fs(sf))));
            SafeImGui.SetItemTooltip(line);
            Clip(dl, sf, Fs(sf), new Vector2(left, y), color, line, right - left);
        }
    }

    private void StackButtons(ImDrawListPtr dl, QuantityView q, QuantityController ctl, int value, ref float x, float y, float h, float right)
    {
        QtySubject s = q.Subject;
        bool reach = s.Where is QtyWhere.Carried or QtyWhere.Open;
        int split = QuantityRules.SplitCount(value, s.Stack);
        QtyPack? into = q.Target;
        string splitTip = !q.CanSplit ? QtyReplies.NoSplitApi
            : q.Busy ? QtyReplies.Busy
            : s.Where == QtyWhere.Wielded ? QtyReplies.Wielded(s.Name)
            : !reach ? QtyReplies.NotReachable(s.Name)
            : into is { Free: 0 } full ? QtyReplies.PackFull(full.Where)
            : $"Split {QuantityRules.N(split)} off the stack of {QuantityRules.N(s.Stack)} into {into?.Where ?? "the main pack"}: a new stack there."
              + (value >= s.Stack ? " One always stays in the stack." : string.Empty);
        bool canSplit = q.CanSplit && !q.Busy && reach;
        if (Chip(dl, "##qty_split", "Split " + QuantityRules.N(split), ref x, y, h, right, canSplit, splitTip, primary: true))
            QuantityAction?.Invoke(QtyClick.Split, split);

        // Where the split goes.
        if (into is { } t)
        {
            string free = t.Free >= 0 ? " · " + t.Free.ToString(System.Globalization.CultureInfo.InvariantCulture) : string.Empty;
            string label = "into " + t.Label + free;
            float before = x;
            if (Chip(dl, "##qty_into_btn", label, ref x, y, h, right, q.Targets.Length > 1 && !q.Busy,
                    q.Targets.Length > 1 ? "Where the split goes: the main pack, a side pack or the container you have open (the number is its free slots)."
                    : "Where the split goes (your only pack).", textColor: t.Free == 0 ? BadColor : null, caret: q.Targets.Length > 1))
                ImGui.OpenPopup(IntoPopupId);
            if (x == before && right - x > Px(40))
            {
                // No room for the whole label: the pack's name cut short.
                if (Chip(dl, "##qty_into_btn2", "into...", ref x, y, h, right, q.Targets.Length > 1 && !q.Busy, "Into " + t.Label + free + ".", caret: q.Targets.Length > 1))
                    ImGui.OpenPopup(IntoPopupId);
            }
            IntoMenu(q, ctl);
        }

        // Give to the last player or NPC selected.
        int give = QuantityRules.GiveCount(value, s.Stack);
        bool canGive = q.CanGive && !q.Busy && s.Where == QtyWhere.Carried && q.GiveTo != 0;
        string giveTip = !q.CanGive ? QtyReplies.NoGiveApi
            : q.Busy ? QtyReplies.Busy
            : s.Where == QtyWhere.Wielded ? QtyReplies.Wielded(s.Name)
            : s.Where != QtyWhere.Carried ? QtyReplies.NotCarried(s.Name)
            : q.GiveTo == 0 ? QtyReplies.NoGiveTarget
            : $"Give {QuantityRules.N(give)} {s.Name} to {q.GiveName} (the last player or NPC you selected).";
        if (Chip(dl, "##qty_give", "Give " + QuantityRules.N(give), ref x, y, h, right, canGive, giveTip))
            QuantityAction?.Invoke(QtyClick.Give, give);
    }

    private void IntoMenu(QuantityView q, QuantityController ctl)
    {
        if (!ImGui.IsPopupOpen(IntoPopupId)) return;
        ImGui.PushStyleColor(ImGuiCol.PopupBg, WithAlpha(_bg, 0xFA));
        ImGui.PushStyleColor(ImGuiCol.Border, _line);
        ImGui.PushStyleVar(ImGuiStyleVar.PopupRounding, Px(4));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(Px(6), Px(5)));
        bool open = ImGui.BeginPopup(IntoPopupId);
        ImGui.PopStyleVar(2);
        ImGui.PopStyleColor(2);
        if (!open) return;
        try
        {
            ImGui.TextDisabled("Split into");
            ImGui.Separator();
            for (int i = 0; i < q.Targets.Length; i++)
            {
                QtyPack p = q.Targets[i];
                string free = p.Free < 0 ? "" : p.Free == 0 ? "  (full)" : $"  ({p.Free} free)";
                // A menu item's label is drawn as it is (not a printf format); "##" keeps two packs of one name apart.
                if (ImGui.MenuItem(p.Label + free + "##qty_into_" + i, null, i == q.TargetIndex, p.Free != 0))
                    ctl.Into = p.Kind == QtyPackKind.Main ? 0 : p.Id;
            }
        }
        finally { ImGui.EndPopup(); }
    }

    private void VendorButtons(ImDrawListPtr dl, QuantityView q, int value, ref float x, float y, float h, float right)
    {
        QtySubject s = q.Subject;
        VendorQuote? quote = s.Quote;
        string? why = quote?.Refusal(value);
        string? bank = quote?.BankNote(value);
        string tip = q.Busy ? QtyReplies.Busy
            : why ?? (quote != null ? $"Buy {QuantityRules.N(value)} {s.Name} for {QuantityRules.N(quote.Cost(value))} {quote.Currency}. "
                                      + (bank != null ? bank + " " : string.Empty) + "The vendor checks again before it sells." : "Buy.");
        if (Chip(dl, "##qty_buy", "Buy " + QuantityRules.N(value), ref x, y, h, right, !q.Busy, tip, primary: why == null))
            QuantityAction?.Invoke(QtyClick.Buy, value);
        if (quote == null) return;
        ImFontPtr sf = Font(RynthUiFont.Narrow9);
        string price = quote.PriceLine(value);
        Clip(dl, sf, Fs(sf), new Vector2(x + Px(2), MathF.Round(y + (h - Fs(sf)) * 0.5f)), why != null ? WarnColor : _dim, price, right - x - Px(2));
    }

    private void Hint(QuantityView q, int value, out string line, out uint color)
    {
        QtySubject s = q.Subject;
        color = _faint;
        if (s.Mode == QtyMode.Vendor)
        {
            string? why = s.Quote?.Refusal(value);
            if (why != null) { line = why; color = WarnColor; return; }
            // The bank's part: paid from a bank that's read, or the server's to decide.
            string? bank = s.Quote?.BankNote(value);
            if (bank != null) { line = bank; color = s.Quote!.BankUnknown ? WarnColor : _dim; return; }
            line = s.Quote is { UnitPrice: > 0 } qq ? $"{QuantityRules.N(qq.UnitPrice)} {qq.Currency} each. /rdef buy N does the same." : "/rdef buy N does the same.";
            return;
        }
        line = s.Where switch
        {
            QtyWhere.Wielded => QtyReplies.Wielded(s.Name),
            QtyWhere.Open => "In the open container: Split takes them into your pack.",
            QtyWhere.Carried when q.GiveTo != 0 => $"Give goes to {q.GiveName}.",
            QtyWhere.Carried => "Split makes a new stack. To give: select the NPC, then this.",
            _ => QtyReplies.NotReachable(s.Name),
        };
        if (s.Where is QtyWhere.Wielded or QtyWhere.Elsewhere or QtyWhere.Unknown) color = WarnColor;
    }

    /// <summary>The number: typed (keys stay out of the game while it has the keyboard), 1 or more.</summary>
    private void NumberField(QuantityController ctl, Vector2 p, float w, float h, QtySubject s)
    {
        ImDrawListPtr dl = ImGui.GetWindowDrawList();
        Vector2 size = new(w, h);
        dl.AddRectFilled(p, p + size, _inset, Px(3));
        ImFontPtr f = Font(RynthUiFont.Dash10);
        ImGui.PushFont(f);
        ImGui.SetWindowFontScale(_r);
        float fs = ImGui.GetFontSize();
        ImGui.PushStyleColor(ImGuiCol.FrameBg, 0u);
        ImGui.PushStyleColor(ImGuiCol.FrameBgHovered, 0u);
        ImGui.PushStyleColor(ImGuiCol.FrameBgActive, 0u);
        ImGui.PushStyleColor(ImGuiCol.Border, 0u);
        ImGui.PushStyleColor(ImGuiCol.Text, _text);
        ImGui.PushStyleColor(ImGuiCol.TextSelectedBg, A(_accent, 0.45f));
        ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, 0f);
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(Px(4), Math.Max(0f, (h - fs) * 0.5f)));
        bool active, hovered;
        try
        {
            ImGui.SetCursorScreenPos(p);
            ImGui.SetNextItemWidth(w);
            int v = ctl.Value;
            if (ImGui.InputInt("##qty_n", ref v, 0, 0, ImGuiInputTextFlags.AutoSelectAll | ImGuiInputTextFlags.CharsDecimal) && v >= 1)
                ctl.Value = v;
            active = ImGui.IsItemActive();
            hovered = ImGui.IsItemHovered();
            SafeImGui.SetItemTooltip(s.Mode == QtyMode.Vendor
                ? "How many to buy. Type it, or pick one beside it."
                : "How many: Split and Give use this number. Type it, or pick one beside it.");
        }
        finally
        {
            ImGui.PopStyleVar(2);
            ImGui.PopStyleColor(6);
            ImGui.SetWindowFontScale(1f);
            ImGui.PopFont();
        }
        dl.AddRect(p, p + size, active ? _accent : hovered ? A(_accent, 0.6f) : _line, Px(3));
    }

    /// <summary>A small flat button at x (moved past it); left out (false) when it doesn't fit before <paramref name="right"/>.</summary>
    private bool Chip(ImDrawListPtr dl, string id, string label, ref float x, float y, float h, float right, bool enabled, string tip,
        bool primary = false, uint? textColor = null, bool caret = false)
    {
        ImFontPtr f = Font(RynthUiFont.Narrow10);
        float tw = f.CalcTextSizeA(Fs(f), float.MaxValue, 0f, label).X;
        float caretW = caret ? Px(9) : 0f;
        float w = MathF.Ceiling(tw + caretW + Px(10));
        if (x + w > right + 0.5f) return false;
        Vector2 p = new(MathF.Round(x), y), size = new(w, h);
        ImGui.SetCursorScreenPos(p);
        bool clicked = ImGui.InvisibleButton(id, size);
        bool hot = enabled && ImGui.IsItemHovered();
        if (tip.Length > 0) SafeImGui.SetItemTooltip(tip);
        uint fill = primary ? A(_accent, hot ? 0.45f : 0.28f) : hot ? _key : A(_key, 0.6f);
        dl.AddRectFilled(p, p + size, enabled ? fill : A(_key, 0.25f), Px(3));
        dl.AddRect(p, p + size, hot ? _accent : A(_line, enabled ? 1f : 0.5f), Px(3));
        uint tc = !enabled ? _faint : textColor ?? (hot || primary ? _text : _dim);
        float tx = MathF.Round(p.X + (w - tw - caretW) * 0.5f);
        dl.AddText(f, Fs(f), new Vector2(tx, MathF.Round(p.Y + (h - Fs(f)) * 0.5f)), tc, label);
        if (caret)
        {
            // A small down arrow after the label.
            float cx = tx + tw + Px(5), cy = p.Y + h * 0.5f, r = Px(2.5f);
            dl.AddTriangleFilled(new Vector2(cx - r, cy - r * 0.5f), new Vector2(cx + r, cy - r * 0.5f), new Vector2(cx, cy + r * 0.7f), tc);
        }
        x += w + Px(QChipGap);
        return clicked && enabled;
    }
}

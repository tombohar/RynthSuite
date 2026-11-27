// RynthSort - Window/SortWindow.Plan.cs: the plan column. Next moves (picture, name, the rule,
// where it goes), Left alone (why, where it would go, Sort it), rules for packs not carried, and
// the activity feed with times; "Every item is in its pack" when nothing waits.

using System;
using System.Numerics;
using ImGuiNET;
using RynthCore.Plugin.RynthSort.Screen;
using RynthCore.Plugin.Shared;
using RynthCore.Plugin.Shared.RynthUi;
using RynthCore.PluginSdk;
using static RynthCore.Plugin.Shared.RynthUi.RynthUi;

namespace RynthCore.Plugin.RynthSort.Window;

internal sealed unsafe partial class SortWindow
{
    private const string HeldText = "You used these lately, so they stay where you put them.";
    private const string OrphanText = "Their items stay where they are. Remove them, or send their kinds to a pack you carry.";

    private float PlanColumn(ImDrawListPtr dl, Vector2 p, float w)
    {
        float pad = D(16), x = p.X + pad, cw = w - 2 * pad, y = p.Y + pad;
        ImFontPtr small = Font(RynthUiFont.Ui10);

        // Next moves.
        y = Eyebrow(dl, new Vector2(x, y), "NEXT MOVES", Num(_m.MovesCount));
        if (_m.MovesCount > 0)
        {
            for (int i = 0; i < _m.Moves.Length; i++)
                y = Row(dl, _m.Moves[i], new Vector2(x, y), cw, RowKind.Move, 100 + i) + D(2);
            if (_m.MovesCount > _m.Moves.Length)
            {
                Text(dl, small, new Vector2(x + D(8), y + D(2)), Ink3, MoreText(_m.MovesCount - _m.Moves.Length));
                y += Fs(small) + D(6);
            }
            if (_m.OrderHint.Length > 0) y = Wrapped(dl, small, new Vector2(x, y + D(6)), cw, Ink3, _m.OrderHint);
            if (_m.MovesHint.Length > 0) y = Wrapped(dl, small, new Vector2(x, y + D(6)), cw, Ink3, _m.MovesHint);
        }
        else
        {
            float h = D(44);
            dl.AddRectFilled(new Vector2(x, y), new Vector2(x + cw, y + h), A(Teal, 0.14f), D(12));
            DrawIcon(dl, RynthIcons.Check, new Vector2(x + D(12), y), new Vector2(D(18), h), D(18), DoneInk);
            Text(dl, Font(RynthUiFont.Ui11), new Vector2(x + D(40), y + (h - Fs(Font(RynthUiFont.Ui11))) * 0.5f), DoneInk, "Every item is in its pack.");
            y += h;
            if (_m.OrderHint.Length > 0) y = Wrapped(dl, small, new Vector2(x, y + D(6)), cw, Ink3, _m.OrderHint);
        }
        y += D(18);

        // Left alone.
        if (_m.Held.Length > 0)
        {
            y = Eyebrow(dl, new Vector2(x, y), "LEFT ALONE", _m.HoldNote);
            float top = y, iw = cw - 2 * D(12);
            float textH = small.CalcTextSizeA(Fs(small), float.MaxValue, iw, HeldText).Y;
            float h = D(12) + textH + D(8) + _m.Held.Length * (RowHeight + D(2)) + D(10);
            dl.AddRectFilled(new Vector2(x, top), new Vector2(x + cw, top + h), A(Amber, 0.12f), D(12));
            dl.AddRect(new Vector2(x, top), new Vector2(x + cw, top + h), A(Amber, 0.25f), D(12));
            dl.AddText(small, Fs(small), Round(new Vector2(x + D(12), top + D(12))), HeldInk, HeldText, iw);
            float ry = top + D(12) + textH + D(8);
            for (int i = 0; i < _m.Held.Length; i++)
                ry = Row(dl, _m.Held[i], new Vector2(x + D(4), ry), cw - D(8), RowKind.Held, 400 + i) + D(2);
            y = top + h + D(18);
        }

        // Rules for packs not carried.
        if (_m.Orphans.Length > 0)
        {
            y = Eyebrow(dl, new Vector2(x, y), "RULES FOR PACKS YOU DON'T CARRY", Num(_m.Orphans.Length));
            float top = y, iw = cw - 2 * D(12);
            float textH = small.CalcTextSizeA(Fs(small), float.MaxValue, iw, OrphanText).Y;
            float chipsH = OrphanChipsHeight(iw);
            float h = D(12) + textH + D(8) + chipsH + D(12);
            dl.AddRectFilled(new Vector2(x, top), new Vector2(x + cw, top + h), A(Amber, 0.12f), D(12));
            dl.AddRect(new Vector2(x, top), new Vector2(x + cw, top + h), A(Amber, 0.25f), D(12));
            dl.AddText(small, Fs(small), Round(new Vector2(x + D(12), top + D(12))), HeldInk, OrphanText, iw);
            OrphanChips(dl, new Vector2(x + D(12), top + D(12) + textH + D(8)), iw);
            y = top + h + D(18);
        }

        // Activity.
        y = Eyebrow(dl, new Vector2(x, y), "ACTIVITY", string.Empty);
        if (_m.Feed.Length == 0)
        {
            Text(dl, small, new Vector2(x, y), Ink3, "Nothing has happened yet.");
            y += Fs(small);
        }
        else
            for (int i = 0; i < _m.Feed.Length; i++)
                y = Row(dl, _m.Feed[i], new Vector2(x, y), cw, RowKind.Feed, 700 + i) + D(2);
        return y - p.Y + pad;
    }

    private enum RowKind { Move, Held, Feed }

    private float RowHeight => D(40);

    /// <summary>One list row (.row): picture, name, the line under it, and what is on the right. Returns the y under it.</summary>
    private float Row(ImDrawListPtr dl, RowModel r, Vector2 p, float w, RowKind kind, int id)
    {
        float h = RowHeight;
        Vector2 size = new(w, h);
        ImGui.PushID(id);
        try
        {
            bool hot = false, clicked = false;
            float rightW = 0;
            ImFontPtr nf = Font(RynthUiFont.Ui11), sf = Font(RynthUiFont.Ui10), mono = Font(RynthUiFont.Dash10), time = Font(RynthUiFont.Dash9);

            // Left alone: a "Sort it" button on the right (it lets RynthSort move the item).
            if (kind == RowKind.Held)
            {
                float bw = ButtonWidth("Sort it", false) - D(8);
                Vector2 bp = new(p.X + w - bw - D(4), p.Y + (h - D(26)) * 0.5f);
                if (SmallButton("##sortit", "Sort it", bp, new Vector2(bw, D(26)), "Stop leaving it alone: the next sort moves it like any other item."))
                    _act.Release(r.ItemId);
                rightW = bw + D(10);
            }
            else if (kind == RowKind.Move)
            {
                rightW = Measure(mono, "→ ").X + Measure(mono, r.Right).X + D(4);
                rightW = Math.Min(rightW, w * 0.45f);
            }
            else rightW = Measure(time, r.Right).X + D(4);

            if (kind != RowKind.Feed && r.ItemId != 0)
            {
                ImGui.SetCursorScreenPos(p);
                clicked = ImGui.InvisibleButton("##row", new Vector2(Math.Max(1, w - (kind == RowKind.Held ? rightW : 0)), h));
                hot = ImGui.IsItemHovered() && _dragItem == 0;
                if (r.Tip.Length > 0) SafeImGui.SetItemTooltip(r.Tip);
            }
            if (hot) dl.AddRectFilled(p, p + size, Card, D(9));

            // The picture (the item's own, else a square in its kind's colour; a feed line about a sort: a check).
            float mini = D(28);
            Vector2 mp = new(p.X + D(8), p.Y + (h - mini) * 0.5f);
            IntPtr tex = r.ItemId != 0 ? _host.GetIconTexture(RynthIconKind.Object, r.ItemId) : IntPtr.Zero;
            if (tex != IntPtr.Zero) dl.AddImageRounded(tex, mp, mp + new Vector2(mini, mini), Vector2.Zero, Vector2.One, White, D(6));
            else
            {
                dl.AddRectFilled(mp, mp + new Vector2(mini, mini), r.KindColor != 0 ? Kinds2Abgr(r.KindColor) : FeedDot, D(6));
                if (r.ItemId == 0) DrawIcon(dl, RynthIcons.Check, mp, new Vector2(mini, mini), D(15), A(White, 0.92f));
            }
            dl.AddRect(mp, mp + new Vector2(mini, mini), A(Black, 0.5f), D(6));

            float tx = mp.X + mini + D(10), tw = Math.Max(10, p.X + w - rightW - D(8) - tx);
            if (kind == RowKind.Feed)
            {
                TextFit(dl, sf, new Vector2(tx, p.Y + (h - Fs(sf)) * 0.5f), Ink2, r.Name, tw);
                Text(dl, time, new Vector2(p.X + w - rightW, p.Y + (h - Fs(time)) * 0.5f), Ink3, r.Right);
            }
            else
            {
                float lines = Fs(nf) + D(2) + Fs(sf), ty = p.Y + (h - lines) * 0.5f;
                TextFit(dl, nf, new Vector2(tx, ty), Ink, r.Name, tw);
                TextFit(dl, sf, new Vector2(tx, ty + Fs(nf) + D(2)), kind == RowKind.Held ? HeldInk : Ink3, r.Sub, tw);
                if (kind == RowKind.Move)
                {
                    float ax = p.X + w - rightW, ay = p.Y + (h - Fs(mono)) * 0.5f;
                    Text(dl, mono, new Vector2(ax, ay), Sky, "→");
                    float aw = Measure(mono, "→ ").X;
                    TextFit(dl, mono, new Vector2(ax + aw, ay), Sky, r.Right, rightW - aw);
                }
            }
            if (clicked) OpenItem(r.ItemId);
        }
        finally { ImGui.PopID(); }
        return p.Y + h;
    }

    /// <summary>A small quiet button (a row's "Sort it").</summary>
    private bool SmallButton(string id, string label, Vector2 p, Vector2 s, string tip)
    {
        ImGui.SetCursorScreenPos(p);
        bool clicked = ImGui.InvisibleButton(id, s);
        bool hot = ImGui.IsItemHovered();
        SafeImGui.SetItemTooltip(tip);
        ImDrawListPtr dl = ImGui.GetWindowDrawList();
        if (hot) dl.AddRectFilled(p, p + s, CardHot, D(8));
        dl.AddRect(p, p + s, Line2, D(8));
        ImFontPtr f = Font(RynthUiFont.UiBold11);
        float fs = Fs(f) * 0.92f;
        Vector2 ts = f.CalcTextSizeA(fs, float.MaxValue, 0, label);
        dl.AddText(f, fs, Round(p + (s - ts) * 0.5f), Ink, label);
        return clicked;
    }

    private float Wrapped(ImDrawListPtr dl, ImFontPtr f, Vector2 p, float w, uint col, string text)
    {
        dl.AddText(f, Fs(f), Round(p), col, text, w);
        return p.Y + f.CalcTextSizeA(Fs(f), float.MaxValue, w, text).Y;
    }

    // "... and 12 more", made once per count.
    private int _moreCount = -1;
    private string _moreText = string.Empty;

    private string MoreText(int n)
    {
        if (n != _moreCount) { _moreCount = n; _moreText = $"... and {n} more"; }
        return _moreText;
    }

    // Rules whose pack isn't carried: their chips (with x) flowing in the amber card.
    private float OrphanChipsHeight(float w) => OrphanFlow(default, Vector2.Zero, w, draw: false);

    private void OrphanChips(ImDrawListPtr dl, Vector2 p, float w) => OrphanFlow(dl, p, w, draw: true);

    private float OrphanFlow(ImDrawListPtr dl, Vector2 p, float w, bool draw)
    {
        ImFontPtr f = Font(RynthUiFont.Ui10);
        float h = D(26), gap = D(6), x = p.X, y = p.Y;
        if (draw) ImGui.PushID("orphans");
        try
        {
            for (int i = 0; i < _m.Orphans.Length; i++)
            {
                ChipModel c = _m.Orphans[i];
                float cw = ChipWidth(f, c);
                if (x > p.X && x + cw > p.X + w) { x = p.X; y += h + gap; }
                if (draw) Chip(dl, c, new Vector2(x, y), new Vector2(cw, h), i);
                x += cw + gap;
            }
        }
        finally { if (draw) ImGui.PopID(); }
        return y + h - p.Y;
    }
}

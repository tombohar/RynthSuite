// RynthSort - Window/SortWindow.Packs.cs: the body's columns, the main pack, the side pack cards,
// item tiles (real pictures, counts, marks, motion), rule chips, and dragging items onto packs.

using System;
using System.Collections.Generic;
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
    /// <summary>A side pack's slots per row (the design's 6 x 4); fewer only when the card is too narrow.</summary>
    private const int PackColumns = 6;
    /// <summary>The main pack's most slots per row (a wide window shows more per row, so fewer rows).</summary>
    private const int MainColumns = 12;

    /// <summary>How many tiles fit across <paramref name="w"/>, between 3 and <paramref name="max"/>.</summary>
    private int ColumnsFor(float w, int max) => Math.Clamp((int)MathF.Floor((w + TileGap) / (Tile40 + TileGap)), 3, max);

    private bool _bodyHot;
    private Vector2 _clipMin, _clipMax;
    // The side pack cards this frame (screen rectangles, pack n at n-1): where a drag is let go.
    private Vector2[] _cardMin = Array.Empty<Vector2>(), _cardMax = Array.Empty<Vector2>();
    private int _cards;
    /// <summary>The card a drag is over (from the last frame's rectangles): it lights up teal.</summary>
    private int _dropCard;
    /// <summary>The item being dragged inside the window (0 = none).</summary>
    private uint _dragItem;
    /// <summary>An item is being dragged out of AC's own windows (the engine's drop target says).</summary>
    private bool _acDragging;
    /// <summary>
    /// The item RynthCore's Inventory window (or AC's own, carried by the engine's drag bridge) is
    /// dragging as an ImGui payload, 0 = none. The card under the mouse lights up for it too.
    /// </summary>
    private uint _invDragItem;
    /// <summary>The same drop arriving both as the payload and through the drop target opens one set of choices.</summary>
    private readonly DropOnce _dropOnce = new();

    /// <summary>
    /// The ImGui payload RynthCore's Inventory window publishes while it drags an item (a uint object
    /// id): RynthCore.Engine ImGui/Panels/InventoryFace.ItemPayloadType and ImGui/ItemDragBridge.PayloadType
    /// (which also carries a drag out of AC's own item windows when the engine knows the dragged item).
    /// RynthAi's Mini Remote takes the same payload.
    /// </summary>
    internal const string InvPayloadType = "RYNTH_INV_ITEM";
    // A short note by the mouse ("That item isn't in your packs.").
    private string _note = string.Empty;
    private double _noteUntil;

    private float Tile40 => D(40);
    private float TileGap => D(5);

    private void Body(Vector2 min, Vector2 size)
    {
        ImGui.SetCursorScreenPos(min);
        bool visible = ImGui.BeginChild("##rs_body", size, ImGuiChildFlags.None, ImGuiWindowFlags.NoBackground);
        try
        {
            if (visible) BodyContent();
        }
        finally
        {
            ImGui.EndChild();
        }
        if (_note.Length > 0 && _now < _noteUntil) SafeImGui.SetTooltip(_note);
    }

    private void BodyContent()
    {
        Vector2 o = Round(ImGui.GetCursorScreenPos());
        float w = MathF.Floor(ImGui.GetContentRegionAvail().X);
        ImDrawListPtr dl = ImGui.GetWindowDrawList();
        _bodyHot = ImGui.IsWindowHovered(ImGuiHoveredFlags.ChildWindows);
        _clipMin = ImGui.GetWindowPos();
        _clipMax = _clipMin + ImGui.GetWindowSize();

        if (!_m.Ready)
        {
            ImFontPtr f = Font(RynthUiFont.Ui11);
            string text = _m.NotReady.Length > 0 ? _m.NotReady : "Waiting for RynthSort...";
            Vector2 ts = Measure(f, text);
            Text(dl, f, new Vector2(o.X + (w - ts.X) * 0.5f, o.Y + D(40)), Ink3, text);
            ImGui.Dummy(new Vector2(w, D(100)));
            return;
        }

        // A drag in progress: ours (an item tile), or one out of AC's windows.
        ImGuiPayloadPtr pl = ImGui.GetDragDropPayload();
        uint dragging = 0;
        _invDragItem = 0;
        if (pl.NativePtr != null && pl.Data != IntPtr.Zero && pl.DataSize >= sizeof(uint))
        {
            if (pl.IsDataType(PayloadType)) dragging = *(uint*)pl.Data;
            else if (pl.IsDataType(InvPayloadType)) _invDragItem = *(uint*)pl.Data;
        }
        _dragItem = dragging;
        Vector2 mouse = ImGui.GetIO().MousePos;
        _dropCard = dragging != 0 || _acDragging || _invDragItem != 0 ? CardUnder(mouse) : 0;

        float h;
        float top = o.Y;
        if (w >= D(1140))
        {
            float mainW = MathF.Round(Math.Clamp(w * 0.24f, D(280), D(330)));
            float planW = MathF.Round(Math.Clamp(w * 0.25f, D(280), D(340)));
            float packsW = w - mainW - planW;
            float h1 = MainColumn(dl, new Vector2(o.X, top), mainW);
            float h2 = PacksColumn(dl, new Vector2(o.X + mainW, top), packsW, oneColumn: false);
            float h3 = PlanColumn(dl, new Vector2(o.X + mainW + packsW, top), planW);
            h = Math.Max(h1, Math.Max(h2, h3));
            dl.AddLine(new Vector2(o.X + mainW, top), new Vector2(o.X + mainW, top + h), Line);
            dl.AddLine(new Vector2(o.X + mainW + packsW, top), new Vector2(o.X + mainW + packsW, top + h), Line);
        }
        else if (w >= D(740))
        {
            float half = MathF.Floor(w * 0.5f);
            float h1 = MainColumn(dl, new Vector2(o.X, top), half);
            float h2 = PacksColumn(dl, new Vector2(o.X + half, top), w - half, oneColumn: true);
            float row = Math.Max(h1, h2);
            dl.AddLine(new Vector2(o.X + half, top), new Vector2(o.X + half, top + row), Line);
            dl.AddLine(new Vector2(o.X, top + row), new Vector2(o.X + w, top + row), Line);
            float h3 = PlanColumn(dl, new Vector2(o.X, top + row + 1), w);
            h = row + 1 + h3;
        }
        else
        {
            float h1 = MainColumn(dl, new Vector2(o.X, top), w);
            dl.AddLine(new Vector2(o.X, top + h1), new Vector2(o.X + w, top + h1), Line);
            float h2 = PacksColumn(dl, new Vector2(o.X, top + h1 + 1), w, oneColumn: true);
            dl.AddLine(new Vector2(o.X, top + h1 + 1 + h2), new Vector2(o.X + w, top + h1 + 1 + h2), Line);
            float h3 = PlanColumn(dl, new Vector2(o.X, top + h1 + h2 + 2), w);
            h = h1 + h2 + 2 + h3;
        }
        ImGui.SetCursorScreenPos(o);
        ImGui.Dummy(new Vector2(w, Math.Max(1, h)));

        // Let go over a pack card: the drop choices.
        if (dragging != 0 && ImGui.IsMouseReleased(ImGuiMouseButton.Left))
        {
            int card = CardUnder(mouse);
            if (card > 0 && _m.TilesById.TryGetValue(dragging, out TileModel? t))
            {
                if (t.Where == card) Note("It is in that pack already.");
                else OpenDrop(dragging, card);
            }
        }
        AcDropTarget(mouse);
        if (SheetShowing) dl.AddRectFilled(_clipMin, _clipMax, A(Argb(0xFF020508), 0.62f));
    }

    /// <summary>Items dragged out of AC's own windows (the inventory, a pack) can be let go on a pack card too (plugin API v90).</summary>
    private void AcDropTarget(Vector2 mouse)
    {
        if (!_host.HasDropTargets || _cards == 0) return;
        Vector2 a = _cardMin[0], b = _cardMax[0];
        for (int i = 1; i < _cards; i++)
        {
            a = Vector2.Min(a, _cardMin[i]);
            b = Vector2.Max(b, _cardMax[i]);
        }
        // Only what shows: a drop below the window must still reach AC.
        a = Vector2.Max(a, _clipMin);
        b = Vector2.Min(b, _clipMax);
        if (b.X <= a.X || b.Y <= a.Y) { _acDragging = false; return; }
        int under = CardUnder(mouse);
        uint dropped = _host.DropTarget(Title, a, b, under > 0 && !SheetShowing, out _acDragging);
        if (dropped == 0 || under == 0) return;
        DroppedFromOutside(dropped, under);
    }

    /// <summary>
    /// An item let go over a pack card from outside the window (RynthCore's Inventory, AC's own item
    /// windows): the same choices as an item dragged inside the window, once per drop.
    /// </summary>
    private void DroppedFromOutside(uint itemId, int card)
    {
        if (!_dropOnce.Take(itemId, _now) || SheetShowing) return;
        if (!_m.TilesById.TryGetValue(itemId, out TileModel? t)) Note("That isn't a loose item in your packs.");
        else if (t.Where == card) Note("It is in that pack already.");
        else OpenDrop(itemId, card);
    }

    /// <summary>
    /// The card as a drop target for RynthCore's Inventory payload (<see cref="InvPayloadType"/>):
    /// an invisible item over the whole card, drawn after its own buttons, so it takes nothing else.
    /// </summary>
    private void CardDropTarget(int number, Vector2 p, Vector2 size)
    {
        if (_invDragItem == 0) return;
        ImGui.SetCursorScreenPos(p);
        ImGui.Dummy(size);
        if (!ImGui.BeginDragDropTarget()) return;
        try
        {
            ImGuiPayloadPtr got = ImGui.AcceptDragDropPayload(InvPayloadType, ImGuiDragDropFlags.AcceptNoDrawDefaultRect);
            if (got.NativePtr != null && got.Data != IntPtr.Zero && got.DataSize >= sizeof(uint))
                DroppedFromOutside(*(uint*)got.Data, number);
        }
        finally { ImGui.EndDragDropTarget(); }
    }

    // ── Folding (0.6.1) ──────────────────────────────────────────────────
    // A click shows at once; the tick saves it and the next model carries it. Until then the
    // window's own answer wins (key 0 = the main pack).
    private readonly Dictionary<uint, bool> _folds = new();
    private int _foldsVersion = -1;

    private bool Folded(uint key, bool model)
    {
        if (_foldsVersion != _m.Version) { _foldsVersion = _m.Version; DropAgreedFolds(); }
        return _folds.TryGetValue(key, out bool v) ? v : model;
    }

    private bool Folded(PackModel pk) => Folded(pk.Id, pk.Collapsed);
    private bool MainFolded => Folded(0, _m.Main.Collapsed);

    private void Fold(PackModel pk, bool folded)
    {
        _folds[pk.Id] = folded;
        _act.SetCollapsed(pk.Id, folded);
    }

    private void FoldMain(bool folded)
    {
        _folds[0] = folded;
        _act.SetMainCollapsed(folded);
    }

    private void FoldAll(bool folded)
    {
        foreach (PackModel pk in _m.Packs) _folds[pk.Id] = folded;
        _act.SetAllCollapsed(folded);
    }

    // The model's answers that agree with the window's: forget those (no allocation: few entries).
    private readonly List<uint> _agreed = new();

    private void DropAgreedFolds()
    {
        if (_folds.Count == 0) return;
        _agreed.Clear();
        foreach (KeyValuePair<uint, bool> kv in _folds)
        {
            bool model = kv.Key == 0 ? _m.Main.Collapsed : PackCollapsed(kv.Key, kv.Value);
            if (model == kv.Value) _agreed.Add(kv.Key);
        }
        foreach (uint k in _agreed) _folds.Remove(k);
    }

    /// <summary>The model's fold for pack <paramref name="id"/>; a pack no longer carried counts as agreeing.</summary>
    private bool PackCollapsed(uint id, bool fallback)
    {
        foreach (PackModel pk in _m.Packs) if (pk.Id == id) return pk.Collapsed;
        return fallback;
    }

    private void Note(string text)
    {
        _note = text;
        _noteUntil = _now + 2.5;
    }

    private int CardUnder(Vector2 m)
    {
        if (m.X < _clipMin.X || m.Y < _clipMin.Y || m.X > _clipMax.X || m.Y > _clipMax.Y) return 0;
        for (int i = 0; i < _cards; i++)
            if (m.X >= _cardMin[i].X && m.Y >= _cardMin[i].Y && m.X < _cardMax[i].X && m.Y < _cardMax[i].Y) return i + 1;
        return 0;
    }

    // ── The main pack ────────────────────────────────────────────────────

    private const string MainHint = "Items no rule names stay here. Drag an item onto a pack to send its kind there, or click it.";

    private float MainColumn(ImDrawListPtr dl, Vector2 p, float w)
    {
        float pad = D(16), inner = D(14);
        float x = p.X + pad, y = p.Y + pad, cw = w - 2 * pad, iw = cw - 2 * inner;
        MainModel main = _m.Main;
        float tile = Tile40, gap = TileGap;
        int cols = ColumnsFor(iw - D(12), MainColumns);
        int slots = MainSlots(cols);
        int rows = Math.Max(1, (slots + cols - 1) / cols);
        float gridH = rows * tile + (rows - 1) * gap;
        float gridW = cols * tile + (cols - 1) * gap;
        float shownH = Math.Min(gridH, D(360));
        ImFontPtr hf = Font(RynthUiFont.Ui10), bold = Font(RynthUiFont.UiBold11), nf = Font(RynthUiFont.Dash10);
        float hintH = hf.CalcTextSizeA(Fs(hf), float.MaxValue, iw, MainHint).Y;
        float handyH = main.Handy.Length > 0 ? D(6) + hf.CalcTextSizeA(Fs(hf), float.MaxValue, iw, main.Handy).Y : 0f;
        float headH = Fs(bold);
        bool folded = MainFolded;
        float cardH = folded
            ? inner + headH + D(12) + D(6) + inner
            : inner + headH + D(12) + D(6) + D(12) + shownH + D(12) + hintH + handyH + inner;

        dl.AddRectFilled(new Vector2(x, y), new Vector2(x + cw, y + cardH), Card, D(14));
        dl.AddRect(new Vector2(x, y), new Vector2(x + cw, y + cardH), Line, D(14));
        float cx = x + inner, cy = y + inner;
        Text(dl, bold, new Vector2(cx, cy), Ink, "Main pack");
        Text(dl, nf, new Vector2(cx + Measure(bold, "Main pack").X + D(8), cy + (headH - Fs(nf)) * 0.5f + D(1)), Ink2, main.Count);
        float fold = D(24);
        if (IconButton("##rs_foldmain", folded ? RynthIcons.CaretRight : RynthIcons.CaretDown, new Vector2(cx + iw - fold, cy + (headH - fold) * 0.5f), new Vector2(fold, fold),
                folded ? "Show the main pack's slots" : "Fold the main pack down to its name", quiet: true))
            FoldMain(!folded);
        cy += headH + D(12);
        Meter(dl, new Vector2(cx, cy), iw, main.Fill, main.NearlyFull, main.Full);
        if (folded) return pad + cardH + pad;
        cy += D(6) + D(12);

        if (gridH > shownH + 0.5f)
        {
            // Taller than the room it gets: a grid of its own that scrolls.
            ImGui.SetCursorScreenPos(new Vector2(cx - D(4), cy));
            bool open = ImGui.BeginChild("##rs_maingrid", new Vector2(Math.Min(iw + D(8), gridW + D(18)), shownH), ImGuiChildFlags.None, ImGuiWindowFlags.NoBackground);
            try
            {
                if (open)
                {
                    Vector2 go = Round(ImGui.GetCursorScreenPos()) + new Vector2(D(4), 0);
                    ImDrawListPtr gdl = ImGui.GetWindowDrawList();
                    MainGrid(gdl, go, tile, gap, cols);
                    ImGui.SetCursorScreenPos(go);
                    ImGui.Dummy(new Vector2(gridW, gridH));
                }
            }
            finally { ImGui.EndChild(); }
        }
        else MainGrid(dl, new Vector2(cx, cy), tile, gap, cols);
        cy += shownH + D(12);
        dl.AddText(hf, Fs(hf), Round(new Vector2(cx, cy)), Ink3, MainHint, iw);
        if (main.Handy.Length > 0)
        {
            cy += hintH + D(6);
            dl.AddText(hf, Fs(hf), Round(new Vector2(cx, cy)), DoneInk, main.Handy, iw);
        }
        return pad + cardH + pad;
    }

    /// <summary>The main pack's items and wells, the last row filled out to <paramref name="cols"/> (never past its capacity).</summary>
    private int MainSlots(int cols)
    {
        MainModel main = _m.Main;
        int slots = main.Tiles.Length + main.Wells;
        int filled = (slots + cols - 1) / cols * cols;
        return Math.Max(main.Tiles.Length, Math.Min(filled, Math.Max(main.Capacity, main.Tiles.Length)));
    }

    private void MainGrid(ImDrawListPtr dl, Vector2 o, float tile, float gap, int cols)
    {
        MainModel main = _m.Main;
        int slots = MainSlots(cols);
        for (int i = 0; i < slots; i++)
        {
            Vector2 at = new(MathF.Round(o.X + i % cols * (tile + gap)), MathF.Round(o.Y + i / cols * (tile + gap)));
            if (!ImGui.IsRectVisible(at, at + new Vector2(tile, tile))) continue;
            if (i < main.Tiles.Length) Tile(dl, main.Tiles[i], at, tile);
            else EmptySlot(dl, at, tile);
        }
    }

    // ── Side packs ───────────────────────────────────────────────────────

    private float PacksColumn(ImDrawListPtr dl, Vector2 p, float w, bool oneColumn)
    {
        float pad = D(16), gap = D(14);
        float x = p.X + pad, y = p.Y + pad, cw = w - 2 * pad;
        float ey = y;
        y = Eyebrow(dl, new Vector2(x, y), "SIDE PACKS", _m.PacksNote);
        if (_m.Packs.Length > 1)
        {
            bool allFolded = true;
            foreach (PackModel pk in _m.Packs) allFolded &= Folded(pk);
            string label = allFolded ? "Open all" : "Fold all";
            ImFontPtr lf = Font(RynthUiFont.Ui10);
            Vector2 ls = Measure(lf, label);
            Vector2 lp = new(x + cw - ls.X, ey + (y - D(10) - ey - ls.Y) * 0.5f);
            ImGui.SetCursorScreenPos(lp - new Vector2(D(4), D(2)));
            bool click = ImGui.InvisibleButton("##rs_foldall", ls + new Vector2(D(8), D(4)));
            bool hot = ImGui.IsItemHovered();
            SafeImGui.SetItemTooltip(allFolded ? "Show every side pack's slots" : "Fold every side pack down to its name and rules");
            Text(dl, lf, lp, hot ? TealHot : Teal, label);
            if (click) FoldAll(!allFolded);
        }

        if (_m.NoRules && _m.Packs.Length > 0) y = NoRulesHint(dl, new Vector2(x, y), cw) + D(12);
        else if (_m.Packs.Length > 0)
        {
            ImFontPtr hf = Font(RynthUiFont.Ui10);
            dl.AddText(hf, Fs(hf), Round(new Vector2(x, y)), Ink3, RulesHint, cw);
            y += hf.CalcTextSizeA(Fs(hf), float.MaxValue, cw, RulesHint).Y + D(12);
        }
        PackModel[] packs = _m.Packs;
        if (_cardMin.Length != packs.Length)
        {
            _cardMin = new Vector2[packs.Length];
            _cardMax = new Vector2[packs.Length];
        }
        _cards = 0;
        if (packs.Length == 0)
        {
            ImFontPtr f = Font(RynthUiFont.Ui10);
            const string none = "You carry no side packs, so items stay in the main pack.";
            dl.AddText(f, Fs(f), Round(new Vector2(x, y)), Ink3, none, cw);
            return y - p.Y + f.CalcTextSizeA(Fs(f), float.MaxValue, cw, none).Y + pad;
        }
        int perRow = oneColumn ? 1 : Math.Max(1, (int)MathF.Floor((cw + gap) / (D(300) + gap)));
        float cardW = MathF.Floor((cw - gap * (perRow - 1)) / perRow);
        for (int i = 0; i < packs.Length; i += perRow)
        {
            float rowH = 0;
            for (int j = i; j < packs.Length && j < i + perRow; j++) rowH = Math.Max(rowH, CardHeight(packs[j], cardW));
            for (int j = i; j < packs.Length && j < i + perRow; j++)
            {
                Vector2 cp = new(x + (j - i) * (cardW + gap), y);
                _cardMin[j] = cp;
                _cardMax[j] = cp + new Vector2(cardW, rowH);
                _cards = j + 1;
                if (ImGui.IsRectVisible(cp, cp + new Vector2(cardW, rowH)))
                {
                    PackCard(dl, packs[j], cp, new Vector2(cardW, rowH));
                    CardDropTarget(packs[j].Number, cp, new Vector2(cardW, rowH));
                }
            }
            y += rowH + gap;
        }
        return y - gap - p.Y + pad;
    }

    private const string RulesHint = "Each pack takes what its rules name. Use + Add a rule on a pack, or drag an item onto one.";

    private const string NoRulesText = "No rules yet, so nothing goes to your side packs. A starter layout fills them in one click.";

    private float NoRulesHint(ImDrawListPtr dl, Vector2 p, float w)
    {
        ImFontPtr f = Font(RynthUiFont.Ui10);
        float bw = ButtonWidth("Pick a layout", true), pad = D(12);
        float tw = w - 2 * pad - bw - D(12);
        float th = f.CalcTextSizeA(Fs(f), float.MaxValue, tw, NoRulesText).Y;
        float h = Math.Max(th, D(32)) + 2 * pad;
        dl.AddRectFilled(p, p + new Vector2(w, h), A(Teal, 0.14f), D(12));
        dl.AddText(f, Fs(f), Round(new Vector2(p.X + pad, p.Y + (h - th) * 0.5f)), DoneInk, NoRulesText, tw);
        if (Button("##rs_pick", "Pick a layout", RynthIcons.SquaresFour, new Vector2(p.X + w - pad - bw, p.Y + (h - D(32)) * 0.5f), D(32), Look.Primary, true,
                "Everyday, Hunting, Crafting or Just the basics, fitted to your packs. You see what would move first.", out _))
            OpenLayouts("everyday");
        return p.Y + h;
    }

    private float CardHeight(PackModel pk, float w)
    {
        float inner = D(14), iw = w - 2 * inner;
        float h = inner + D(34) + D(11) + D(6) + D(11) + Chips(default, pk, Vector2.Zero, iw, draw: false);
        if (!Folded(pk))
        {
            int slots = Math.Max(pk.Capacity, pk.Tiles.Length + pk.Ghosts.Length);
            int cols = ColumnsFor(iw, PackColumns);
            int rows = Math.Max(1, (slots + cols - 1) / cols);
            h += D(12) + rows * Tile40 + (rows - 1) * TileGap;
        }
        if (pk.JobLine.Length > 0)
        {
            ImFontPtr jf = Font(RynthUiFont.Ui10);
            h += jf.CalcTextSizeA(Fs(jf), float.MaxValue, iw, pk.JobLine).Y + D(9);
        }
        if (pk.Warnings.Length > 0)
        {
            ImFontPtr f = Font(RynthUiFont.Ui10);
            h += D(8);
            foreach (string line in pk.Warnings) h += f.CalcTextSizeA(Fs(f), float.MaxValue, iw, line).Y + D(2);
        }
        return h + inner;
    }

    private void PackCard(ImDrawListPtr dl, PackModel pk, Vector2 p, Vector2 size)
    {
        bool drop = _dropCard == pk.Number;
        dl.AddRectFilled(p, p + size, drop ? DropBg : Card, D(14));
        dl.AddRect(p, p + size, drop ? Teal : Line, D(14), ImDrawFlags.None, drop ? 2f : 1f);
        ImGui.PushID(pk.Number);
        try
        {
            float inner = D(14), iw = size.X - 2 * inner;
            float x = p.X + inner, y = p.Y + inner;

            // Header: the bag badge (selects the pack in the game), the names, "+N coming", the count.
            float bag = D(34);
            Vector2 bp = new(x, y);
            if (_m.CanSelect)
            {
                ImGui.SetCursorScreenPos(bp);
                if (ImGui.InvisibleButton("##bag", new Vector2(bag, bag))) _act.SelectInGame(pk.Id);
                SafeImGui.SetItemTooltip("Select this pack in the game, as clicking it in your inventory would, so you can see which one it is. Nothing moves.");
            }
            Bag(dl, bp, bag, pk.Badge);
            ImFontPtr nf = Font(RynthUiFont.Dash10), pf = Font(RynthUiFont.Narrow11);
            float right = x + iw;
            bool folded = Folded(pk);
            float fold = D(24);
            if (IconButton("##fold", folded ? RynthIcons.CaretRight : RynthIcons.CaretDown, new Vector2(right - fold, y + (bag - fold) * 0.5f), new Vector2(fold, fold),
                    folded ? "Show this pack's slots" : "Fold this pack down to its name and rules", quiet: true))
                Fold(pk, !folded);
            right -= fold + D(6);
            float countW = Measure(nf, pk.Count).X;
            Text(dl, nf, new Vector2(right - countW, y + (bag - Fs(nf)) * 0.5f), Ink2, pk.Count);
            right -= countW + D(10);
            right = JobButton(dl, pk, right, y, bag) - D(8);
            if (pk.Incoming.Length > 0)
            {
                float iwid = Measure(pf, pk.Incoming).X + D(16), ih = Fs(pf) + D(9);
                Vector2 ip = new(right - iwid, y + (bag - ih) * 0.5f);
                dl.AddRectFilled(ip, ip + new Vector2(iwid, ih), A(Sky, 0.14f), ih * 0.5f);
                Text(dl, pf, new Vector2(ip.X + D(8), ip.Y + (ih - Fs(pf)) * 0.5f), Sky, pk.Incoming);
                right -= iwid + D(8);
            }
            ImFontPtr bold = Font(RynthUiFont.UiBold11), small = Font(RynthUiFont.Ui10);
            float tx = x + bag + D(10), tw = Math.Max(10, right - tx);
            float lines = Fs(bold) + D(2) + Fs(small), ty = y + (bag - lines) * 0.5f;
            TextFit(dl, bold, new Vector2(tx, ty), Ink, pk.Name, tw);
            TextFit(dl, small, new Vector2(tx, ty + Fs(bold) + D(2)), Ink3, pk.Where, tw);
            if (_bodyHot && _dragItem == 0 && ImGui.IsMouseHoveringRect(new Vector2(tx, y), new Vector2(tx + tw, y + bag)))
                SafeImGui.SetTooltip(pk.Tip);
            y += bag + D(11);

            Meter(dl, new Vector2(x, y), iw, pk.Fill, pk.NearlyFull, pk.Full);
            y += D(6) + D(11);
            if (pk.JobLine.Length > 0)
            {
                ImFontPtr jf = Font(RynthUiFont.Ui10);
                dl.AddText(jf, Fs(jf), Round(new Vector2(x, y)), pk.JobProblem ? Amber : DoneInk, pk.JobLine, iw);
                y += jf.CalcTextSizeA(Fs(jf), float.MaxValue, iw, pk.JobLine).Y + D(9);
            }

            // Its rules first (what goes here, and "+ Add a rule"), so they show on a folded card too.
            y += Chips(dl, pk, new Vector2(x, y), iw, draw: true);

            // The slots: its items, the ghosts of what a sort would bring, then empty wells.
            if (!Folded(pk))
            {
                y += D(12);
                float tile = Tile40, gap = TileGap;
                int slots = Math.Max(pk.Capacity, pk.Tiles.Length + pk.Ghosts.Length);
                int cols = ColumnsFor(iw, PackColumns);
                for (int i = 0; i < slots; i++)
                {
                    Vector2 at = new(MathF.Round(x + i % cols * (tile + gap)), MathF.Round(y + i / cols * (tile + gap)));
                    if (!ImGui.IsRectVisible(at, at + new Vector2(tile, tile))) continue;
                    if (i < pk.Tiles.Length) Tile(dl, pk.Tiles[i], at, tile);
                    else if (i < pk.Tiles.Length + pk.Ghosts.Length) Ghost(dl, pk.Ghosts[i - pk.Tiles.Length], at, tile, i);
                    else EmptySlot(dl, at, tile);
                }
                int rows = Math.Max(1, (slots + cols - 1) / cols);
                y += rows * tile + (rows - 1) * gap;
            }
            if (pk.Warnings.Length > 0)
            {
                ImFontPtr f = Font(RynthUiFont.Ui10);
                y += D(8);
                foreach (string line in pk.Warnings)
                {
                    dl.AddText(f, Fs(f), Round(new Vector2(x, y)), Amber, line, iw);
                    y += f.CalcTextSizeA(Fs(f), float.MaxValue, iw, line).Y + D(2);
                }
            }
        }
        finally { ImGui.PopID(); }
    }

    /// <summary>
    /// The card header's job control, right-aligned at <paramref name="right"/>: the job's pill
    /// (Salvage, Sell, Mule) or, with none, a small button. Opens the job popup. Returns its left edge.
    /// </summary>
    private float JobButton(ImDrawListPtr dl, PackModel pk, float right, float y, float rowH)
    {
        ImFontPtr pf = Font(RynthUiFont.Narrow11);
        float h = Fs(pf) + D(9);
        bool has = pk.JobLabel.Length > 0;
        string icon = pk.Job switch { Sorting.JobKind.Salvage => RynthIcons.Hammer, Sorting.JobKind.Sell => RynthIcons.Coins, Sorting.JobKind.Mule => RynthIcons.User, _ => RynthIcons.Package };
        float w = has ? D(8) + D(14) + D(5) + Math.Min(Measure(pf, pk.JobLabel).X, D(120)) + D(9) : h;
        Vector2 p = new(right - w, y + (rowH - h) * 0.5f), s = new(w, h);
        ImGui.SetCursorScreenPos(p);
        bool clicked = ImGui.InvisibleButton("##job", s);
        bool hot = ImGui.IsItemHovered();
        SafeImGui.SetItemTooltip(has
            ? "This pack's job: " + pk.JobLabel + ". Click to change it."
            : "Give this pack a job: what RynthAi would salvage, sell, or give your mule goes here.");
        uint tone = pk.JobProblem ? Amber : Brass;
        if (has)
        {
            dl.AddRectFilled(p, p + s, A(tone, hot ? 0.24f : 0.14f), h * 0.5f);
            DrawIcon(dl, icon, new Vector2(p.X + D(8), p.Y), new Vector2(D(14), h), D(13), tone);
            TextFit(dl, pf, new Vector2(p.X + D(8) + D(14) + D(5), p.Y + (h - Fs(pf)) * 0.5f), tone, pk.JobLabel, D(120));
        }
        else
        {
            if (hot) dl.AddRectFilled(p, p + s, CardHot, h * 0.5f);
            DrawIcon(dl, icon, p, s, D(13), hot ? Ink : Ink3);
        }
        if (clicked) OpenJob(pk.Number);
        return p.X;
    }

    // ── Rule chips ───────────────────────────────────────────────────────

    private const string AddKindLabel = "+ Add a kind…", AddRuleLabel = "+ Add a rule", NothingHere = "No rules yet: nothing goes here.";

    /// <summary>The pack's chips, then "+ Add a kind...", flowing onto as many lines as they need. Returns the height; draws only when <paramref name="draw"/>.</summary>
    private float Chips(ImDrawListPtr dl, PackModel pk, Vector2 p, float w, bool draw)
    {
        ImFontPtr f = Font(RynthUiFont.Ui10);
        float h = D(26), gap = D(6), x = p.X, y = p.Y;
        void Advance(float itemW)
        {
            if (x > p.X && x + itemW > p.X + w) { x = p.X; y += h + gap; }
        }
        for (int i = 0; i < pk.Chips.Length; i++)
        {
            ChipModel c = pk.Chips[i];
            float cw = ChipWidth(f, c);
            Advance(cw);
            if (draw) Chip(dl, c, new Vector2(x, y), new Vector2(cw, h), i);
            x += cw + gap;
        }
        if (pk.Chips.Length == 0)
        {
            float tw = Measure(f, NothingHere).X;
            Advance(tw);
            if (draw) Text(dl, f, new Vector2(x, y + (h - Fs(f)) * 0.5f), Ink3, NothingHere);
            x += tw + gap;
        }
        float aw = Measure(f, AddRuleLabel).X + D(20);
        Advance(aw);
        if (draw)
        {
            Vector2 ap = new(x, y), asz = new(aw, h);
            ImGui.SetCursorScreenPos(ap);
            bool clicked = ImGui.InvisibleButton("##addkind", asz);
            bool hot = ImGui.IsItemHovered();
            SafeImGui.SetItemTooltip("Say what goes to this pack: type part of a name, or pick a kind of item. You can also drag an item onto the pack.");
            dl.AddRectFilled(ap, ap + asz, A(Teal, hot ? 0.20f : 0.10f), h * 0.5f);
            dl.AddRect(ap, ap + asz, A(Teal, hot ? 1f : 0.55f), h * 0.5f);
            Text(dl, f, new Vector2(ap.X + D(10), ap.Y + (h - Fs(f)) * 0.5f), hot ? TealHot : Teal, AddRuleLabel);
            if (clicked) OpenKinds(pk.Number, new Vector2(ap.X, ap.Y + h + D(4)));
        }
        return y + h - p.Y;
    }

    private float ChipWidth(ImFontPtr f, ChipModel c) =>
        D(9) + (c.IsName ? 0 : D(8) + D(6)) + Measure(f, c.Label).X + D(6) + D(18) + D(6);

    private void Chip(ImDrawListPtr dl, ChipModel c, Vector2 p, Vector2 s, int index)
    {
        ImGui.PushID(index);
        try
        {
            ImFontPtr f = Font(RynthUiFont.Ui10);
            float alpha = c.Off ? 0.5f : 1f;
            dl.AddRectFilled(p, p + s, Card2, s.Y * 0.5f);
            if (c.IsName) Dashed(dl, p, p + s, Line2, D(3));
            else dl.AddRect(p, p + s, Line2, s.Y * 0.5f);
            float x = p.X + D(9);
            if (!c.IsName)
            {
                dl.AddCircleFilled(new Vector2(x + D(4), p.Y + s.Y * 0.5f), D(4), A(Kinds2Abgr(c.Dot), alpha), 12);
                x += D(8) + D(6);
            }
            Text(dl, f, new Vector2(x, p.Y + (s.Y - Fs(f)) * 0.5f), A(Ink, alpha), c.Label);

            // The chip's body: its tooltip (and, for a rule that is off, a click turns it on); then the x.
            Vector2 xs = new(D(18), D(18));
            Vector2 xp = new(p.X + s.X - D(6) - xs.X, p.Y + (s.Y - xs.Y) * 0.5f);
            ImGui.SetCursorScreenPos(p);
            bool body = ImGui.InvisibleButton("##chip", new Vector2(Math.Max(1, xp.X - p.X), s.Y));
            SafeImGui.SetItemTooltip(c.Tip);
            if (body && c.Off) _act.TurnOn(c.RuleUid);
            ImGui.SetCursorScreenPos(xp);
            bool x2 = ImGui.InvisibleButton("##x", xs);
            bool hot = ImGui.IsItemHovered();
            SafeImGui.SetItemTooltip(c.IsName ? "Stop sending items with this name here (Undo brings it back)" : "Stop sending this kind here (Undo brings it back)");
            if (hot) dl.AddCircleFilled(xp + xs * 0.5f, xs.X * 0.5f, Argb(0xFF24344A), 16);
            DrawIcon(dl, RynthIcons.X, xp, xs, D(11), hot ? Ink : Ink3);
            if (x2) _act.RemoveChip(c.RuleUid, c.IsName, c.Cls, c.Name);
        }
        finally { ImGui.PopID(); }
    }

    /// <summary>A model colour (ARGB) as ImGui's ABGR.</summary>
    private static uint Kinds2Abgr(uint argb) => Argb(argb);

    // ── Tiles ────────────────────────────────────────────────────────────

    // Tooltips made once per tile (and model).
    private readonly Dictionary<uint, string> _tips = new();
    private int _tipsVersion = -1;

    private string TileTip(TileModel t)
    {
        if (_tipsVersion != _m.Version) { _tips.Clear(); _tipsVersion = _m.Version; }
        if (_tips.TryGetValue(t.Id, out string? tip)) return tip;
        tip = t.Name + (t.Stack > 1 ? " (" + t.Stack + ")" : string.Empty)
              + (t.Held ? "\nLeft alone: " + t.HeldWhy : t.Settling ? "\nNew: it settles before it moves" : t.Outgoing ? "\nA sort would move it" : string.Empty)
              + "\nClick for more. Drag it onto a pack to send it there.";
        _tips[t.Id] = tip;
        return tip;
    }

    /// <summary>One item: its picture (or its kind's colour while the picture is made), count, marks and motion.</summary>
    private void Tile(ImDrawListPtr dl, TileModel t, Vector2 p, float s)
    {
        ImGui.PushID(unchecked((int)t.Id));
        try
        {
            ImGui.SetCursorScreenPos(p);
            bool clicked = ImGui.InvisibleButton("##t", new Vector2(s, s));
            bool hot = ImGui.IsItemHovered() && _dragItem == 0;
            IntPtr tex = _host.GetIconTexture(RynthIconKind.Object, t.Id);
            if (ImGui.BeginDragDropSource(ImGuiDragDropFlags.None))
            {
                uint id = t.Id;
                ImGui.SetDragDropPayload(PayloadType, (IntPtr)(&id), sizeof(uint));
                if (tex != IntPtr.Zero)
                {
                    ImGui.Image(tex, new Vector2(D(34), D(34)));
                    ImGui.SameLine();
                }
                SafeImGui.Text(t.Name);
                ImGui.TextDisabled("Let go over a pack");
                ImGui.EndDragDropSource();
            }
            if (hot) SafeImGui.SetTooltip(TileTip(t));
            if (clicked && _dragItem == 0 && !ImGui.IsMouseDragging(ImGuiMouseButton.Left)) OpenItem(t.Id);

            // Motion: a tile a move is about to send lifts and pulses; one that just arrived pops in.
            float pulse = 0.5f - 0.5f * MathF.Cos((float)(_now * Math.PI * 2 / 1.4));
            float lift = t.Moving ? D(4) * pulse : hot ? D(1) : 0f;
            float pop = _arrivals.Progress(t.Id, _now);
            float grow = pop < 1f ? 0.6f + 0.4f * EaseOut(pop) : 1f;
            float alpha = (_searching && !_hits.Contains(t.Id) ? 0.28f : 1f) * (pop < 1f ? 0.2f + 0.8f * pop : 1f);
            Vector2 c = p + new Vector2(s * 0.5f, s * 0.5f - lift);
            Vector2 half = new(s * 0.5f * grow, s * 0.5f * grow);
            Vector2 min = Round(c - half), max = Round(c + half);
            float r = D(7) * grow;

            dl.AddRectFilled(min + new Vector2(0, D(2)), max + new Vector2(0, D(2)), A(Black, (t.Moving ? 0.5f : 0.3f) * alpha), r);
            if (tex != IntPtr.Zero)
            {
                dl.AddRectFilled(min, max, A(Well, alpha), r);
                dl.AddImageRounded(tex, min + new Vector2(1, 1), max - new Vector2(1, 1), Vector2.Zero, Vector2.One, A(White, alpha), Math.Max(0, r - 1));
            }
            else
            {
                uint kind = Kinds2Abgr(t.KindColor);
                dl.AddRectFilled(min, max, A(kind, alpha), r);
                dl.AddRectFilled(new Vector2(min.X, (min.Y + max.Y) * 0.5f), max, A(Darken(Darken(kind)), alpha * 0.6f), r, ImDrawFlags.RoundCornersBottom);
                ImFontPtr af = Font(RynthUiFont.Narrow10);
                Vector2 ts = af.CalcTextSizeA(Fs(af), float.MaxValue, 0, t.Initials);
                dl.AddText(af, Fs(af), Round(c - ts * 0.5f), A(White, 0.92f * alpha), t.Initials);
            }
            dl.AddRect(min, max, A(Black, 0.5f * alpha), r);
            dl.AddLine(new Vector2(min.X + r, min.Y + 1), new Vector2(max.X - r, min.Y + 1), A(White, 0.18f * alpha));
            if (hot) dl.AddRect(min, max, A(White, 0.25f), r);

            bool hit = _searching && _hits.Contains(t.Id);
            bool sel = _sheet == Sheet.Item && _sheetItem == t.Id;
            if (hit) Glow(dl, min, max, r, Brass);
            if (sel) Glow(dl, min, max, r, Teal);
            if (t.Moving) dl.AddRect(min - new Vector2(1, 1), max + new Vector2(1, 1), A(Sky, 0.5f + 0.5f * pulse), r + 1, ImDrawFlags.None, 2f);

            if (t.Qty.Length > 0)
            {
                ImFontPtr qf = Font(RynthUiFont.Dash9);
                Vector2 qs = qf.CalcTextSizeA(Fs(qf), float.MaxValue, 0, t.Qty);
                Vector2 qp = Round(new Vector2(max.X - qs.X - D(2), max.Y - qs.Y - D(1)));
                dl.AddText(qf, Fs(qf), qp + new Vector2(1, 1), A(Black, 0.9f * alpha), t.Qty);
                dl.AddText(qf, Fs(qf), qp, A(White, alpha), t.Qty);
            }
            if (t.Held) Badge(dl, new Vector2(max.X - D(3), min.Y + D(3)), Amber, Argb(0xFF3A2A06), RynthIcons.Lock, alpha);
            else if (t.Moving) Badge(dl, new Vector2(max.X - D(3), min.Y + D(3)), Sky, Argb(0xFF06223A), RynthIcons.ArrowRight, alpha);
            if (t.Settling) Spinner(dl, min, max);
        }
        finally { ImGui.PopID(); }
    }

    private static float EaseOut(float t) => 1f - (1f - t) * (1f - t) * (1f - t);

    private void Glow(ImDrawListPtr dl, Vector2 min, Vector2 max, float r, uint col)
    {
        dl.AddRect(min - new Vector2(D(4), D(4)), max + new Vector2(D(4), D(4)), A(col, 0.18f), r + D(4), ImDrawFlags.None, D(4));
        dl.AddRect(min - new Vector2(1, 1), max + new Vector2(1, 1), col, r + 1, ImDrawFlags.None, 2f);
    }

    /// <summary>A small round mark on a tile's top-right corner (.badge): left alone (a lock), or on its way (an arrow).</summary>
    private void Badge(ImDrawListPtr dl, Vector2 c, uint fill, uint ink, string icon, float alpha)
    {
        float rad = D(8);
        dl.AddCircleFilled(c, rad + D(2), A(Card, alpha), 16);
        dl.AddCircleFilled(c, rad, A(fill, alpha), 16);
        DrawIcon(dl, icon, c - new Vector2(rad, rad), new Vector2(2 * rad, 2 * rad), D(10), A(ink, alpha));
    }

    /// <summary>The settling ring (.settling::after): an amber arc running round the tile once a second.</summary>
    private void Spinner(ImDrawListPtr dl, Vector2 min, Vector2 max)
    {
        Vector2 a = min - new Vector2(D(3), D(3)), b = max + new Vector2(D(3), D(3));
        dl.AddRect(a, b, A(Amber, 0.18f), D(9), ImDrawFlags.None, 2f);
        float t0 = (float)(_now % 1.0) * 4f;
        const int steps = 10;
        for (int i = 0; i <= steps; i++)
        {
            float t = t0 + 0.9f * i / steps;
            dl.PathLineTo(Perimeter(a, b, t));
        }
        dl.PathStroke(Amber, ImDrawFlags.None, 2f);
    }

    /// <summary>A point <paramref name="t"/> sides (0-4, wrapping) round the box from its top-left corner, clockwise.</summary>
    private static Vector2 Perimeter(Vector2 a, Vector2 b, float t)
    {
        t %= 4f;
        if (t < 0) t += 4f;
        int side = (int)t;
        float f = t - side;
        return side switch
        {
            0 => new Vector2(a.X + (b.X - a.X) * f, a.Y),
            1 => new Vector2(b.X, a.Y + (b.Y - a.Y) * f),
            2 => new Vector2(b.X - (b.X - a.X) * f, b.Y),
            _ => new Vector2(a.X, b.Y - (b.Y - a.Y) * f),
        };
    }

    /// <summary>A slot an item a sort would bring will take (.ghost).</summary>
    private void Ghost(ImDrawListPtr dl, GhostModel g, Vector2 p, float s, int index)
    {
        Vector2 max = p + new Vector2(s, s);
        dl.AddRectFilled(p, max, A(Sky, 0.14f), D(7));
        Dashed(dl, p, max, Sky, D(3.5f), 1.5f);
        DrawIcon(dl, RynthIcons.ArrowDown, p, new Vector2(s, s), D(16), A(Sky, 0.85f));
        if (_bodyHot && _dragItem == 0 && ImGui.IsMouseHoveringRect(p, max)) SafeImGui.SetTooltip(g.Tip);
    }

    /// <summary>An empty slot (.slot): a dark well.</summary>
    private void EmptySlot(ImDrawListPtr dl, Vector2 p, float s)
    {
        Vector2 max = p + new Vector2(s, s);
        dl.AddRectFilled(p, max, Well, D(7));
        dl.AddLine(new Vector2(p.X + D(5), p.Y + 1), new Vector2(max.X - D(5), p.Y + 1), A(Black, 0.6f));
        dl.AddRect(p, max, A(White, 0.02f), D(7));
    }
}

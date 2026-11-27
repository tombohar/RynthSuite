// RynthSort - Window/SortWindow.Sheets.cs: the popups over the window. One sheet at a time
// (an item's detail, the drop choices, the layouts, the settings), opened and drawn at the
// window's own level (never from inside the body's child windows, so the popup id always
// matches), centred on the window, and the kind picker under a pack's "+ Add a kind...".
// Their words are made when they open.

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
    private const string SheetId = "##rs_sheet", KindsId = "##rs_kinds";

    private enum Sheet { None, Item, Drop, Layouts, Settings, Job }

    private Sheet _sheet, _pendingSheet;
    private uint _sheetItem;
    private int _sheetPack;
    private float _sheetH;
    private bool _closeSheet;
    private string _sheetTitle = string.Empty;

    private bool SheetShowing => _sheet != Sheet.None;

    private void OpenSheet(Sheet s) => _pendingSheet = s;

    private void OpenItem(uint itemId)
    {
        if (!_m.TilesById.TryGetValue(itemId, out TileModel? t)) return;
        _sheetItem = itemId;
        _sheetTitle = t.Name;
        _whyKey = default;
        OpenSheet(Sheet.Item);
    }

    // The drop choices' words (made when it opens).
    private string _dropKind = string.Empty, _dropKindText = string.Empty, _dropName = string.Empty, _dropOnceText = string.Empty;

    private void OpenDrop(uint itemId, int pack)
    {
        if (!_m.TilesById.TryGetValue(itemId, out TileModel? t)) return;
        PackModel? pk = _m.Pack(pack);
        if (pk == null) return;
        _sheetItem = itemId;
        _sheetPack = pack;
        _sheetTitle = "Send " + t.Name + " to " + PackWords(pk);
        _dropKind = "All " + t.KindLabel.ToLowerInvariant();
        _dropKindText = $"A rule: every item of this kind you carry or loot goes to {PackWords(pk)}. The kind leaves any other pack.";
        _dropName = "Only “" + AddSelectedName(t.Name) + "”";
        int hold = _m.Settings.HoldMin;
        _dropOnceText = hold > 0
            ? $"It goes there now and is left alone for {hold} minute{(hold == 1 ? "" : "s")}, so a sort won’t pull it back."
            : "It goes there now. Leaving items alone is off (Settings), so a sort may move it back.";
        OpenSheet(Sheet.Drop);
    }

    /// <summary>A pack in a sentence: its name, with its number when another pack has the same name.</summary>
    private static string PackWords(PackModel pk) => pk.Label.Length > 0 ? pk.Label : pk.Name;

    /// <summary>The name a name rule keeps (the item's name without a "(100)" count).</summary>
    private static string AddSelectedName(string name) => Views.AddSelectedRules.EntryFor(name);

    private string? _layoutPicked;

    private void OpenLayouts(string? picked)
    {
        _layoutPicked = picked;
        _sheetTitle = "Start from a layout";
        OpenSheet(Sheet.Layouts);
        _act.WantLayouts(true);
    }

    private int _kindsPack;
    private Vector2 _kindsAnchor;
    private bool _openKinds;
    private string _kindsTitle = string.Empty, _kindsAddTip = string.Empty, _kindsText = string.Empty;

    private void OpenKinds(int pack, Vector2 anchor)
    {
        PackModel? pk = _m.Pack(pack);
        if (pk == null) return;
        _kindsPack = pack;
        _kindsAnchor = anchor;
        _kindsTitle = "What goes to " + PackWords(pk) + "?";
        _kindsText = string.Empty;
        _kindsAddTip = "Select an item in your inventory first: its name goes into this pack's rules, so items called that go here. Also: /rsort add " + pk.Badge;
        _openKinds = true;
    }

    private void CloseSheet()
    {
        if (_sheet == Sheet.Layouts) _act.WantLayouts(false);
        _sheet = Sheet.None;
        _sheetItem = 0;
    }

    /// <summary>Opens what was asked for this frame, then draws the open sheet and the kind picker (at the window's level).</summary>
    private void Sheets()
    {
        if (_pendingSheet != Sheet.None)
        {
            if (_sheet == Sheet.Layouts && _pendingSheet != Sheet.Layouts) _act.WantLayouts(false);
            _sheet = _pendingSheet;
            _pendingSheet = Sheet.None;
            _sheetH = 0;
            if (_sheet == Sheet.Settings) { _sheetTitle = "Settings"; CopySettings(); }
            ImGui.OpenPopup(SheetId);
        }
        if (_openKinds)
        {
            _openKinds = false;
            ImGui.OpenPopup(KindsId);
        }
        KindsPopup();
        if (_sheet == Sheet.None) return;

        float width = Math.Min(_sheet switch { Sheet.Layouts => D(860), Sheet.Settings => D(640), _ => D(600) }, Math.Max(D(260), _winSize.X - D(24)));
        float maxH = Math.Max(_winSize.Y - D(24), D(320));
        float h = _sheetH > 0 ? Math.Min(_sheetH, maxH) : Math.Min(D(360), maxH);
        ImGui.SetNextWindowPos(_winMin + _winSize * 0.5f, ImGuiCond.Always, new Vector2(0.5f, 0.5f));
        ImGui.SetNextWindowSize(new Vector2(width, h), ImGuiCond.Always);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        bool open = ImGui.BeginPopup(SheetId, ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoSavedSettings);
        ImGui.PopStyleVar();
        if (!open)
        {
            CloseSheet();
            return;
        }
        try
        {
            _closeSheet = false;
            Vector2 o = Round(ImGui.GetCursorScreenPos());
            float w = MathF.Floor(ImGui.GetContentRegionAvail().X);
            ImDrawListPtr dl = ImGui.GetWindowDrawList();
            float y = SheetHeader(dl, o, w, _sheetTitle);
            float x = o.X + D(18), cw = w - D(36);
            y += D(16);
            y = _sheet switch
            {
                Sheet.Item => ItemSheet(dl, x, y, cw),
                Sheet.Drop => DropSheet(dl, x, y, cw),
                Sheet.Layouts => LayoutsSheet(dl, x, y, cw),
                Sheet.Job => JobSheet(dl, x, y, cw),
                _ => SettingsSheet(dl, x, y, cw),
            };
            y += D(18);
            ImGui.SetCursorScreenPos(o);
            ImGui.Dummy(new Vector2(w, y - o.Y));
            _sheetH = y - o.Y + 2;
            if (_closeSheet)
            {
                ImGui.CloseCurrentPopup();
                CloseSheet();
            }
        }
        finally { ImGui.EndPopup(); }
    }

    /// <summary>The sheet's title row and its close button (.sheet header). Returns the y under it.</summary>
    private float SheetHeader(ImDrawListPtr dl, Vector2 o, float w, string title)
    {
        float h = D(56);
        ImFontPtr f = Font(RynthUiFont.UiBold14);
        TextFit(dl, f, new Vector2(o.X + D(18), o.Y + (h - Fs(f)) * 0.5f), Ink, title, w - D(36) - D(40));
        if (IconButton("##rs_sheetx", RynthIcons.X, new Vector2(o.X + w - D(18) - D(32), o.Y + (h - D(32)) * 0.5f), new Vector2(D(32), D(32)), "Close"))
            _closeSheet = true;
        dl.AddLine(new Vector2(o.X, o.Y + h), new Vector2(o.X + w, o.Y + h), Line);
        return o.Y + h;
    }

    // ── An item's detail ─────────────────────────────────────────────────

    // The "why" line laid out in words (bold runs too), made when it opens or the item's state changes.
    private readonly List<(string Text, bool Bold, Vector2 At)> _why = new();
    private (TileRule, int, int, bool, int) _whyKey;
    private float _whyH;

    private float ItemSheet(ImDrawListPtr dl, float x, float y, float w)
    {
        ImFontPtr small = Font(RynthUiFont.Ui10);
        if (!_m.TilesById.TryGetValue(_sheetItem, out TileModel? t))
        {
            Text(dl, Font(RynthUiFont.Ui11), new Vector2(x, y), Ink2, "That item isn't in your packs now.");
            return y + Fs(Font(RynthUiFont.Ui11));
        }

        // The picture, the name and count, the kind and where it is.
        float big = D(56);
        Vector2 bp = new(x, y);
        IntPtr tex = _host.GetIconTexture(RynthIconKind.Object, t.Id);
        if (tex != IntPtr.Zero)
        {
            dl.AddRectFilled(bp, bp + new Vector2(big, big), Well, D(12));
            dl.AddImageRounded(tex, bp + new Vector2(D(4), D(4)), bp + new Vector2(big - D(4), big - D(4)), Vector2.Zero, Vector2.One, White, D(8));
        }
        else
        {
            dl.AddRectFilled(bp, bp + new Vector2(big, big), Kinds2Abgr(t.KindColor), D(12));
            ImFontPtr af = Font(RynthUiFont.NarrowBold13);
            Vector2 ts = af.CalcTextSizeA(Fs(af), float.MaxValue, 0, t.Initials);
            dl.AddText(af, Fs(af), Round(bp + (new Vector2(big, big) - ts) * 0.5f), White, t.Initials);
        }
        dl.AddRect(bp, bp + new Vector2(big, big), A(Black, 0.5f), D(12));
        ImFontPtr bold = Font(RynthUiFont.UiBold14), mono = Font(RynthUiFont.Dash10);
        float tx = x + big + D(14), tw = w - big - D(14);
        float ty = y + (big - (Fs(bold) + D(4) + Fs(small))) * 0.5f;
        string qty = QtyText(t.Qty);
        float qw = qty.Length > 0 ? Measure(mono, qty).X + D(8) : 0;
        string name = Fit(bold, t.Name, tw - qw);
        Text(dl, bold, new Vector2(tx, ty), Ink, name);
        if (qty.Length > 0) Text(dl, mono, new Vector2(tx + Measure(bold, name).X + D(8), ty + (Fs(bold) - Fs(mono)) * 0.5f + D(1)), Ink2, qty);
        TextFit(dl, small, new Vector2(tx, ty + Fs(bold) + D(4)), Ink3, KindAndPlace(t), tw);
        y += big + D(14);

        // Why it is where it is.
        LayoutWhy(t, w - D(24));
        dl.AddRectFilled(new Vector2(x, y), new Vector2(x + w, y + _whyH + D(20)), Card, D(10));
        dl.AddRect(new Vector2(x, y), new Vector2(x + w, y + _whyH + D(20)), Line, D(10));
        ImFontPtr wf = Font(RynthUiFont.Ui11), wb = Font(RynthUiFont.UiBold11);
        foreach (var seg in _why)
            Text(dl, seg.Bold ? wb : wf, new Vector2(x + D(12), y + D(10)) + seg.At, seg.Bold ? Ink : Ink2, seg.Text);
        y += _whyH + D(20) + D(14);

        // Send to a side pack (its own is greyed).
        y = Eyebrow(dl, new Vector2(x, y), "SEND TO", string.Empty);
        float gap = D(8), minW = D(130);
        int per = Math.Max(1, (int)MathF.Floor((w + gap) / (minW + gap)));
        float bw = MathF.Floor((w - gap * (per - 1)) / per), bh = D(36);
        for (int i = 0; i < _m.Packs.Length; i++)
        {
            PackModel pk = _m.Packs[i];
            Vector2 p = new(x + i % per * (bw + gap), y + i / per * (bh + gap));
            ImGui.PushID(i);
            bool here = t.Where == pk.Number;
            if (PackButton(dl, p, new Vector2(bw, bh), pk, !here) && !here)
            {
                _act.MoveOnce(t.Id, pk.Number);
                _closeSheet = true;
            }
            ImGui.PopID();
        }
        int rows = (_m.Packs.Length + per - 1) / per;
        if (rows == 0)
        {
            Text(dl, small, new Vector2(x, y), Ink3, "You carry no side packs.");
            y += Fs(small) + D(14);
        }
        else y += rows * (bh + gap) - gap + D(14);

        // Leave it alone / let RynthSort move it; back to the main pack; select it in the game.
        float bx = x, aby = y, abh = D(34);
        bool holdOn = _m.Settings.HoldMin > 0;
        if (t.Held)
        {
            if (Button("##rs_release", "Let RynthSort move it", null, new Vector2(bx, aby), abh, Look.Normal, true,
                    "Stop leaving it alone: the next sort moves it like any other item.", out float w1))
            { _act.Release(t.Id); _closeSheet = true; }
            bx += w1 + D(8);
        }
        else
        {
            if (Button("##rs_hold", HoldLabel(), RynthIcons.Lock, new Vector2(bx, aby), abh, Look.Normal, holdOn,
                    holdOn ? "No sort moves it until the time is up (Settings: Leave items I use alone for)." : "Leaving items alone is off: turn it on in Settings.", out float w1))
            { _act.Hold(t.Id); _closeSheet = true; }
            bx += w1 + D(8);
        }
        if (t.Where != 0)
        {
            if (Button("##rs_tomain", "Back to the main pack", null, new Vector2(bx, aby), abh, Look.Quiet, true,
                    "It goes to the main pack now and is left alone for a while, so a sort doesn't send it back.", out float w2))
            { _act.MoveOnce(t.Id, 0); _closeSheet = true; }
            bx += w2 + D(8);
        }
        if (_m.CanSelect && bx + ButtonWidth("Select in game", false) <= x + w)
        {
            if (Button("##rs_select", "Select in game", null, new Vector2(bx, aby), abh, Look.Quiet, true,
                    "Selects it in the game, as clicking it in your inventory would. Nothing moves.", out _))
                _act.SelectInGame(t.Id);
        }
        return aby + abh;
    }

    // "×320" for the open item, made once per count.
    private string _qtyFor = string.Empty, _qtyText = string.Empty;

    private string QtyText(string qty)
    {
        if (qty.Length == 0) return string.Empty;
        if (!ReferenceEquals(qty, _qtyFor)) { _qtyFor = qty; _qtyText = "×" + qty; }
        return _qtyText;
    }

    // "Leave it alone for 3 min", made once per setting.
    private int _holdLabelFor = -1;
    private string _holdLabel = string.Empty;

    private string HoldLabel()
    {
        int m = _m.Settings.HoldMin;
        if (m != _holdLabelFor) { _holdLabelFor = m; _holdLabel = m > 0 ? $"Leave it alone for {m} min" : "Leave it alone (off)"; }
        return _holdLabel;
    }

    // "Gems · in Satchel" for the open item, made once per place.
    private (uint, int) _kindPlaceFor;
    private string _kindPlace = string.Empty;

    private string KindAndPlace(TileModel t)
    {
        if (_kindPlaceFor != (t.Id, t.Where)) { _kindPlaceFor = (t.Id, t.Where); _kindPlace = ScreenText.KindAndPlace(t, _m); }
        return _kindPlace;
    }

    /// <summary>Lays the "why" runs out in words across <paramref name="w"/> (only when the item's state or the width changes).</summary>
    private void LayoutWhy(TileModel t, float w)
    {
        var key = (t.Rule, t.RulePack * 1000 + t.Where, (int)w, t.Held, t.RuleText.Length);
        if (key == _whyKey && _why.Count > 0) return;
        _whyKey = key;
        _why.Clear();
        ImFontPtr wf = Font(RynthUiFont.Ui11), wb = Font(RynthUiFont.UiBold11);
        float lineH = Math.Max(Fs(wf), Fs(wb)) + D(4);
        float x = 0, y = 0;
        foreach (ScreenText.Run run in ScreenText.Why(t, _m))
        {
            ImFontPtr f = run.Bold ? wb : wf;
            int start = 0;
            string s = run.Text;
            while (start < s.Length)
            {
                int sp = s.IndexOf(' ', start);
                int end = sp < 0 ? s.Length : sp + 1;
                string word = s.Substring(start, end - start);
                float ww = f.CalcTextSizeA(Fs(f), float.MaxValue, 0, word).X;
                float trimmed = f.CalcTextSizeA(Fs(f), float.MaxValue, 0, word.TrimEnd()).X;
                if (x > 0 && x + trimmed > w) { x = 0; y += lineH; }
                _why.Add((word, run.Bold, new Vector2(x, y)));
                x += ww;
                start = end;
            }
        }
        _whyH = y + lineH - D(4);
    }

    /// <summary>A pack button (the detail popup's "Send to"): the bag badge and the pack's name.</summary>
    private bool PackButton(ImDrawListPtr dl, Vector2 p, Vector2 s, PackModel pk, bool enabled)
    {
        ImGui.SetCursorScreenPos(p);
        bool clicked = ImGui.InvisibleButton("##pk", s) && enabled;
        bool hot = enabled && ImGui.IsItemHovered();
        SafeImGui.SetItemTooltip(enabled ? pk.Tip : "It is in this pack.");
        float alpha = enabled ? 1f : 0.45f;
        dl.AddRectFilled(p, p + s, A(hot ? CardHot : Card2, alpha), D(10));
        dl.AddRect(p, p + s, A(Line2, alpha), D(10));
        float bag = D(20);
        Bag(dl, new Vector2(p.X + D(10), p.Y + (s.Y - bag) * 0.5f), bag, pk.Badge);
        ImFontPtr f = Font(RynthUiFont.UiBold11);
        TextFit(dl, f, new Vector2(p.X + D(10) + bag + D(8), p.Y + (s.Y - Fs(f)) * 0.5f), A(Ink, alpha), pk.Name, s.X - bag - D(28));
        return clicked;
    }

    // ── The drop choices ─────────────────────────────────────────────────

    private const string NameRuleText = "A rule by name. A name beats a kind, so these go here even if their kind goes elsewhere.";

    private float DropSheet(ImDrawListPtr dl, float x, float y, float w)
    {
        if (!_m.TilesById.TryGetValue(_sheetItem, out TileModel? t) || _m.Pack(_sheetPack) == null)
        {
            Text(dl, Font(RynthUiFont.Ui11), new Vector2(x, y), Ink2, "That item or pack isn't there now.");
            return y + Fs(Font(RynthUiFont.Ui11));
        }
        IntPtr tex = _host.GetIconTexture(RynthIconKind.Object, t.Id);
        if (Choice(dl, "##rs_kind", new Vector2(x, y), w, null, tex, _dropKind, _dropKindText, out float h))
        { _act.Unhold(t.Id); _act.SendKind(_sheetPack, t.Cls); _closeSheet = true; }
        y += h + D(8);
        if (Choice(dl, "##rs_name", new Vector2(x, y), w, RynthIcons.TextAa, IntPtr.Zero, _dropName, NameRuleText, out h))
        { _act.Unhold(t.Id); _act.SendName(_sheetPack, t.Id); _closeSheet = true; }
        y += h + D(8);
        if (Choice(dl, "##rs_once", new Vector2(x, y), w, RynthIcons.ArrowRight, IntPtr.Zero, "Just move it, no rule", _dropOnceText, out h))
        { _act.MoveOnce(t.Id, _sheetPack); _closeSheet = true; }
        return y + h;
    }

    /// <summary>One of the drop choices (.choice): an icon box, a bold line and a line under it. True when clicked.</summary>
    private bool Choice(ImDrawListPtr dl, string id, Vector2 p, float w, string? glyph, IntPtr tex, string strong, string desc, out float h)
    {
        ImFontPtr bf = Font(RynthUiFont.UiBold11), sf = Font(RynthUiFont.Ui10);
        float pad = D(12), box = D(34), tw = w - 2 * pad - box - D(12);
        float descH = sf.CalcTextSizeA(Fs(sf), float.MaxValue, tw, desc).Y;
        h = Math.Max(box, Fs(bf) + D(3) + descH) + 2 * pad;
        Vector2 s = new(w, h);
        ImGui.SetCursorScreenPos(p);
        bool clicked = ImGui.InvisibleButton(id, s);
        bool hot = ImGui.IsItemHovered();
        dl.AddRectFilled(p, p + s, hot ? DropBg : Card, D(12));
        dl.AddRect(p, p + s, hot ? Teal : Line2, D(12));
        Vector2 bp = new(p.X + pad, p.Y + (h - box) * 0.5f);
        dl.AddRectFilled(bp, bp + new Vector2(box, box), Card2, D(9));
        if (tex != IntPtr.Zero) dl.AddImageRounded(tex, bp + new Vector2(D(3), D(3)), bp + new Vector2(box - D(3), box - D(3)), Vector2.Zero, Vector2.One, White, D(6));
        else if (glyph != null) DrawIcon(dl, glyph, bp, new Vector2(box, box), D(18), Teal);
        float tx = bp.X + box + D(12), ty = p.Y + (h - (Fs(bf) + D(3) + descH)) * 0.5f;
        TextFit(dl, bf, new Vector2(tx, ty), Ink, strong, tw);
        dl.AddText(sf, Fs(sf), Round(new Vector2(tx, ty + Fs(bf) + D(3))), Ink3, desc, tw);
        return clicked;
    }

    // ── A pack's job ─────────────────────────────────────────────────────

    private string _jobMule = string.Empty;

    private void OpenJob(int pack)
    {
        PackModel? pk = _m.Pack(pack);
        if (pk == null) return;
        _sheetPack = pack;
        _sheetTitle = "A job for " + PackWords(pk);
        _jobMule = pk.JobMule;
        OpenSheet(Sheet.Job);
    }

    private const string JobHint = "A job fills the pack with what another tool picks, besides its own rules. A name rule still comes first; a job comes before a kind. Nothing moves until you sort.";

    private float JobSheet(ImDrawListPtr dl, float x, float y, float w)
    {
        PackModel? pk = _m.Pack(_sheetPack);
        if (pk == null)
        {
            Text(dl, Font(RynthUiFont.Ui11), new Vector2(x, y), Ink2, "That pack isn't carried now.");
            return y + Fs(Font(RynthUiFont.Ui11));
        }
        ImFontPtr small = Font(RynthUiFont.Ui10);
        y = Wrapped(dl, small, new Vector2(x, y), w, Ink3, JobHint) + D(12);
        if (pk.JobLine.Length > 0)
            y = Wrapped(dl, small, new Vector2(x, y), w, pk.JobProblem ? Amber : DoneInk, pk.JobLine) + D(12);
        bool Pick(string id, string glyph, string strong, string desc, Sorting.JobKind kind, ref float yy)
        {
            bool now = pk.Job == kind;
            bool clicked = Choice(dl, id, new Vector2(x, yy), w, glyph, IntPtr.Zero, now ? strong + " (now)" : strong, desc, out float h);
            yy += h + D(8);
            return clicked;
        }
        if (Pick("##job_salvage", RynthIcons.Hammer, "Salvage", "Items RynthAi's loot profile marks Salvage. Needs RynthAi.", Sorting.JobKind.Salvage, ref y))
        { _act.SetJob(_sheetPack, (int)Sorting.JobKind.Salvage, string.Empty); _closeSheet = true; }
        if (Pick("##job_sell", RynthIcons.Coins, "Sell", "Items RynthAi's AutoVendor would sell (its default.utl). Needs RynthAi. With AutoVendor set to sell from the main pack only, it won't sell them from here.", Sorting.JobKind.Sell, ref y))
        { _act.SetJob(_sheetPack, (int)Sorting.JobKind.Sell, string.Empty); _closeSheet = true; }
        if (Pick("##job_mule", RynthIcons.User, "Mule", "Items set aside for your mule: what RynthAi's AutoTrade would give it (its <mule>.utl, else default.utl). Needs RynthAi.", Sorting.JobKind.Mule, ref y))
        { _act.SetJob(_sheetPack, (int)Sorting.JobKind.Mule, _jobMule); _closeSheet = true; }
        // The mule's name (optional): AutoTrade's profile named after it, else its default.utl.
        float fh = ImGui.GetFrameHeight();
        ImGui.SetCursorScreenPos(new Vector2(x, y));
        ImGui.SetNextItemWidth(w);
        ImGui.InputTextWithHint("##jobmule", "The mule's name (optional)", ref _jobMule, 64);
        y += fh + D(12);
        if (pk.Job != Sorting.JobKind.None)
        {
            if (Button("##job_none", "No job: only its rules", null, new Vector2(x, y), D(34), Look.Quiet, true,
                    "Take the job off. Items already in the pack stay; Clean sends back what no rule wants.", out _))
            { _act.SetJob(_sheetPack, 0, string.Empty); _closeSheet = true; }
            y += D(34);
        }
        return y;
    }

    // ── Layouts ──────────────────────────────────────────────────────────

    private const string LayoutsHint = "Each kind goes to the pack that already holds most of it, so the fewest items move. Nothing moves until you sort.";
    private readonly Dictionary<string, string> _layoutCaps = new();
    private readonly Dictionary<string, string> _layoutLeft = new();
    private int _layoutLeftVersion = -1;

    private float LayoutsSheet(ImDrawListPtr dl, float x, float y, float w)
    {
        ImFontPtr small = Font(RynthUiFont.Ui10), bold = Font(RynthUiFont.UiBold11), mono = Font(RynthUiFont.Dash10);
        y = Wrapped(dl, small, new Vector2(x, y), w, Ink3, LayoutsHint) + D(14);
        LayoutCard[] cards = _m.Layouts;
        if (_m.Packs.Length == 0)
        {
            Text(dl, Font(RynthUiFont.Ui11), new Vector2(x, y), Amber, "You carry no side packs, so a layout has nowhere to put things.");
            return y + Fs(Font(RynthUiFont.Ui11));
        }
        if (cards.Length == 0)
        {
            Text(dl, small, new Vector2(x, y), Ink3, "Fitting the layouts to your packs…");
            return y + Fs(small);
        }
        float gap = D(10), pad = D(12);
        int per = Math.Max(1, (int)MathF.Floor((w + gap) / (D(260) + gap)));
        float cw = MathF.Floor((w - gap * (per - 1)) / per), iw = cw - 2 * pad;
        float barsH = D(30);
        for (int i = 0; i < cards.Length; i += per)
        {
            float rowH = 0;
            for (int j = i; j < cards.Length && j < i + per; j++)
                rowH = Math.Max(rowH, pad + Fs(bold) + D(8) + small.CalcTextSizeA(Fs(small), float.MaxValue, iw, cards[j].Blurb).Y + D(8) + barsH + D(8) + Fs(mono) + pad);
            for (int j = i; j < cards.Length && j < i + per; j++)
            {
                LayoutCard c = cards[j];
                Vector2 p = new(x + (j - i) * (cw + gap), y), s = new(cw, rowH);
                bool picked = _layoutPicked == c.Key;
                ImGui.PushID(j);
                ImGui.SetCursorScreenPos(p);
                bool clicked = ImGui.InvisibleButton("##lay", s);
                bool hot = ImGui.IsItemHovered();
                SafeImGui.SetItemTooltip(picked ? "Click to close the preview" : "Click to see which pack gets which kinds");
                ImGui.PopID();
                if (clicked) _layoutPicked = picked ? null : c.Key;
                dl.AddRectFilled(p, p + s, picked ? DropBg : Card, D(12));
                dl.AddRect(p, p + s, picked ? Teal : hot ? Argb(0xFF3C5970) : Line2, D(12));
                float cy = p.Y + pad;
                Text(dl, bold, new Vector2(p.X + pad, cy), Ink, c.Name);
                cy += Fs(bold) + D(8);
                dl.AddText(small, Fs(small), Round(new Vector2(p.X + pad, cy)), Ink3, c.Blurb, iw);
                cy += small.CalcTextSizeA(Fs(small), float.MaxValue, iw, c.Blurb).Y + D(8);
                Bars(dl, new Vector2(p.X + pad, cy), iw, barsH, c.Bars);
                cy = p.Y + rowH - pad - Fs(mono);
                Text(dl, mono, new Vector2(p.X + pad, cy), Sky, c.Meta);
            }
            y += rowH + gap;
        }
        y -= gap;

        LayoutCard? chosen = null;
        foreach (LayoutCard c in cards) if (c.Key == _layoutPicked) { chosen = c; break; }
        if (chosen == null) return y;

        // The preview: which pack gets which kinds, what stays in the main pack, then Use / Cancel.
        y += D(14);
        dl.AddLine(new Vector2(x, y), new Vector2(x + w, y), Line);
        y += D(14);
        if (!_layoutCaps.TryGetValue(chosen.Key, out string? caps)) _layoutCaps[chosen.Key] = caps = chosen.Name.ToUpperInvariant() + " ON YOUR PACKS";
        y = Eyebrow(dl, new Vector2(x, y), caps, string.Empty);
        ImFontPtr nf = Font(RynthUiFont.Ui11);
        float nameW = D(110), chipsX = x + D(24) + D(10) + nameW + D(10), chipsW = x + w - chipsX;
        foreach (LayoutRow row in chosen.Rows)
        {
            float rh = Math.Max(D(26), PreviewChips(default, row, Vector2.Zero, chipsW, draw: false));
            Bag(dl, new Vector2(x, y + (rh - D(24)) * 0.5f), D(24), row.Badge);
            TextFit(dl, nf, new Vector2(x + D(24) + D(10), y + (rh - Fs(nf)) * 0.5f), Ink, row.PackName, nameW);
            if (row.Chips.Length == 0) Text(dl, small, new Vector2(chipsX, y + (rh - Fs(small)) * 0.5f), Ink3, "Stays free");
            else PreviewChips(dl, row, new Vector2(chipsX, y), chipsW, draw: true);
            y += rh + D(8);
        }
        if (_layoutLeftVersion != _m.Version) { _layoutLeft.Clear(); _layoutLeftVersion = _m.Version; }
        if (!_layoutLeft.TryGetValue(chosen.Key, out string? left)) _layoutLeft[chosen.Key] = left = chosen.Left + chosen.Replaces;
        y = Wrapped(dl, small, new Vector2(x, y), w, Ink3, left) + D(12);
        if (Button("##rs_uselay", "Use this layout", null, new Vector2(x, y), D(34), Look.Primary, true,
                "Set these rules. Nothing moves: press Sort now afterwards, or turn on Auto-sort. Undo brings your rules back.", out float uw))
        {
            _act.UseLayout(chosen.Key);
            _closeSheet = true;
        }
        if (Button("##rs_laycancel", "Cancel", null, new Vector2(x + uw + D(8), y), D(34), Look.Normal, true, null, out _))
            _layoutPicked = null;
        return y + D(34);
    }

    /// <summary>A layout card's mini bars (.lay .bars): a well per pack with a square per kind.</summary>
    private void Bars(ImDrawListPtr dl, Vector2 p, float w, float h, uint[][] bars)
    {
        int n = Math.Max(1, bars.Length);
        float gap = D(4), bw = (w - gap * (n - 1)) / n, sq = D(7), sgap = D(2);
        for (int i = 0; i < bars.Length; i++)
        {
            Vector2 bp = new(p.X + i * (bw + gap), p.Y);
            dl.AddRectFilled(bp, bp + new Vector2(bw, h), Well, D(6));
            float sx = bp.X + D(4), sy = bp.Y + D(4);
            foreach (uint col in bars[i])
            {
                if (sx + sq > bp.X + bw - D(2)) { sx = bp.X + D(4); sy += sq + sgap; }
                dl.AddRectFilled(new Vector2(sx, sy), new Vector2(sx + sq, sy + sq), Kinds2Abgr(col), D(2));
                sx += sq + sgap;
            }
        }
    }

    /// <summary>A preview row's kind chips (no x). Returns the height; draws only when <paramref name="draw"/>.</summary>
    private float PreviewChips(ImDrawListPtr dl, LayoutRow row, Vector2 p, float w, bool draw)
    {
        ImFontPtr f = Font(RynthUiFont.Ui10);
        float h = D(26), gap = D(6), x = p.X, y = p.Y;
        foreach (ChipModel c in row.Chips)
        {
            float cw = D(9) + D(8) + D(6) + Measure(f, c.Label).X + D(9);
            if (x > p.X && x + cw > p.X + w) { x = p.X; y += h + gap; }
            if (draw)
            {
                Vector2 cp = new(x, y);
                dl.AddRectFilled(cp, cp + new Vector2(cw, h), Card2, h * 0.5f);
                dl.AddRect(cp, cp + new Vector2(cw, h), Line2, h * 0.5f);
                dl.AddCircleFilled(new Vector2(cp.X + D(9) + D(4), cp.Y + h * 0.5f), D(4), Kinds2Abgr(c.Dot), 12);
                Text(dl, f, new Vector2(cp.X + D(9) + D(8) + D(6), cp.Y + (h - Fs(f)) * 0.5f), Ink, c.Label);
            }
            x += cw + gap;
        }
        return y + h - p.Y;
    }

    // ── Settings ─────────────────────────────────────────────────────────

    // What the sliders and boxes show while the sheet is open (copied from the model when it opens).
    private int _dSettle, _dHold, _dGap;
    private bool _dCombat, _dOrder, _dKind, _dHandy;
    private string _handyText = string.Empty;
    private const string HandyKindsId = "##rs_handykinds";

    /// <summary>
    /// The handy list under its switch: its kinds and names as chips (the x takes one off), "+ Add a
    /// kind..." and a box for a part of a name. Returns the y under it.
    /// </summary>
    private float HandyList(ImDrawListPtr dl, float x, float y, float w, bool enabled)
    {
        ImFontPtr f = Font(RynthUiFont.Ui10);
        float h = D(26), gap = D(6), cx = x;
        y += D(10);
        ImGui.PushID("handy");
        try
        {
            if (!enabled) ImGui.BeginDisabled();
            ChipModel[] chips = _m.Settings.HandyChips;
            for (int i = 0; i < chips.Length; i++)
            {
                ChipModel c = chips[i];
                float cw = ChipWidth(f, c);
                if (cx > x && cx + cw > x + w) { cx = x; y += h + gap; }
                if (HandyChip(dl, c, new Vector2(cx, y), new Vector2(cw, h), i))
                {
                    if (c.IsName) _act.HandyName(c.Name, false);
                    else _act.HandyKind(c.Cls, false);
                }
                cx += cw + gap;
            }
            float aw = Measure(f, AddKindLabel).X + D(20);
            if (cx > x && cx + aw > x + w) { cx = x; y += h + gap; }
            Vector2 ap = new(cx, y), asz = new(aw, h);
            ImGui.SetCursorScreenPos(ap);
            bool open = ImGui.InvisibleButton("##handyadd", asz);
            bool hot = ImGui.IsItemHovered();
            SafeImGui.SetItemTooltip("Pick a kind of item to keep at the front of the main pack.");
            if (hot) dl.AddRectFilled(ap, ap + asz, A(Teal, 0.08f), h * 0.5f);
            Dashed(dl, ap, ap + asz, hot ? Teal : Line2, D(4));
            Text(dl, f, new Vector2(ap.X + D(10), ap.Y + (h - Fs(f)) * 0.5f), Teal, AddKindLabel);
            if (open) ImGui.OpenPopup(HandyKindsId);
            HandyKindsPopup(new Vector2(ap.X, ap.Y + h + D(4)));
            y += h + D(10);

            // A part of a name ("Potion"): items with it in their name count whatever their kind.
            float bw = ButtonWidth("Add name", false), fh = ImGui.GetFrameHeight();
            ImGui.SetCursorScreenPos(new Vector2(x, y));
            ImGui.SetNextItemWidth(Math.Max(D(80), w - bw - D(8)));
            bool enter = ImGui.InputTextWithHint("##handyname", "Part of a name, like Potion", ref _handyText, 64, ImGuiInputTextFlags.EnterReturnsTrue);
            bool add = Button("##handyaddname", "Add name", null, new Vector2(x + w - bw, y + (fh - D(30)) * 0.5f), D(30), Look.Normal, _handyText.Trim().Length > 0,
                "Items with this in their name count as handy, whatever their kind.", out _);
            if ((enter || add) && _handyText.Trim().Length > 0)
            {
                _act.HandyName(_handyText.Trim(), true);
                _handyText = string.Empty;
            }
            y += Math.Max(fh, D(30)) + D(12);
            if (!enabled) ImGui.EndDisabled();
        }
        finally { ImGui.PopID(); }
        dl.AddLine(new Vector2(x, y), new Vector2(x + w, y), Line);
        return y;
    }

    /// <summary>A handy-list chip (a kind with its dot, or a name dashed) and its x. True when the x was clicked.</summary>
    private bool HandyChip(ImDrawListPtr dl, ChipModel c, Vector2 p, Vector2 s, int index)
    {
        ImGui.PushID(index);
        try
        {
            ImFontPtr f = Font(RynthUiFont.Ui10);
            dl.AddRectFilled(p, p + s, Card2, s.Y * 0.5f);
            if (c.IsName) Dashed(dl, p, p + s, Line2, D(3));
            else dl.AddRect(p, p + s, Line2, s.Y * 0.5f);
            float tx = p.X + D(9);
            if (!c.IsName)
            {
                dl.AddCircleFilled(new Vector2(tx + D(4), p.Y + s.Y * 0.5f), D(4), Kinds2Abgr(c.Dot), 12);
                tx += D(8) + D(6);
            }
            Text(dl, f, new Vector2(tx, p.Y + (s.Y - Fs(f)) * 0.5f), Ink, c.Label);
            Vector2 xs = new(D(18), D(18));
            Vector2 xp = new(p.X + s.X - D(6) - xs.X, p.Y + (s.Y - xs.Y) * 0.5f);
            ImGui.SetCursorScreenPos(p);
            ImGui.InvisibleButton("##hc", new Vector2(Math.Max(1, xp.X - p.X), s.Y));
            SafeImGui.SetItemTooltip(c.Tip);
            ImGui.SetCursorScreenPos(xp);
            bool x = ImGui.InvisibleButton("##hx", xs);
            bool hot = ImGui.IsItemHovered();
            SafeImGui.SetItemTooltip("Take it off the handy list");
            if (hot) dl.AddCircleFilled(xp + xs * 0.5f, xs.X * 0.5f, Argb(0xFF24344A), 16);
            DrawIcon(dl, RynthIcons.X, xp, xs, D(11), hot ? Ink : Ink3);
            return x;
        }
        finally { ImGui.PopID(); }
    }

    /// <summary>The handy list's kind picker (a popup over the settings sheet).</summary>
    private void HandyKindsPopup(Vector2 anchor)
    {
        if (!ImGui.IsPopupOpen(HandyKindsId)) return;
        ImGui.SetNextWindowPos(anchor, ImGuiCond.Appearing);
        ImGui.SetNextWindowSizeConstraints(new Vector2(D(220), 0), new Vector2(D(320), D(340)));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(D(8), D(8)));
        ImGui.PushStyleVar(ImGuiStyleVar.PopupRounding, D(10));
        bool open = ImGui.BeginPopup(HandyKindsId);
        ImGui.PopStyleVar(2);
        if (!open) return;
        try
        {
            ImFontPtr f = Font(RynthUiFont.Ui11);
            float rowH = Fs(f) + D(10);
            SafeImGui.TextDisabled("Keep at the front of the main pack");
            KindChoice[] kinds = _m.Settings.HandyChoices;
            for (int i = 0; i < kinds.Length; i++)
            {
                KindChoice k = kinds[i];
                ImGui.PushID(i);
                Vector2 p = ImGui.GetCursorScreenPos();
                bool picked = ImGui.Selectable("##hk", false, ImGuiSelectableFlags.None, new Vector2(0, rowH));
                ImGui.PopID();
                ImDrawListPtr pdl = ImGui.GetWindowDrawList();
                pdl.AddCircleFilled(new Vector2(p.X + D(8), p.Y + rowH * 0.5f), D(4), Kinds2Abgr(k.Dot), 12);
                Text(pdl, f, new Vector2(p.X + D(20), p.Y + (rowH - Fs(f)) * 0.5f), Ink, k.Label);
                if (picked) _act.HandyKind(k.Cls, true);
            }
        }
        finally { ImGui.EndPopup(); }
    }

    private void CopySettings()
    {
        SettingsModel s = _m.Settings;
        _dHandy = s.HandyOn;
        _dSettle = s.SettleSec;
        _dHold = s.HoldMin;
        _dGap = s.GapMs;
        _dCombat = s.SortInCombat;
        _dOrder = s.OrderInPacks;
        _dKind = s.KindFirst;
    }

    private static readonly string[] SecondsText = Units(0, 30, " s"), MinutesText = Units(0, 30, " min");

    private static string[] Units(int from, int to, string unit)
    {
        var a = new string[to - from + 1];
        for (int i = from; i <= to; i++) a[i - from] = i.ToString(System.Globalization.CultureInfo.InvariantCulture) + unit;
        return a;
    }

    private int _gapFor = -1;
    private string _gapText = string.Empty;

    private float SettingsSheet(ImDrawListPtr dl, float x, float y, float w)
    {
        y = SliderRow(dl, x, y, w, "Wait before new loot moves", SecondsText[Math.Clamp(_dSettle, 0, 30)], "##settle", ref _dSettle, 0, 30,
            "So you can look at a new item before it goes to its pack.", out bool changed);
        if (changed) _act.SetSettle(_dSettle);
        y = SliderRow(dl, x, y, w, "Leave items I use alone for", _dHold == 0 ? "off" : MinutesText[Math.Clamp(_dHold, 0, 30)], "##hold", ref _dHold, 0, 30,
            "An item you select, drag, move, take off or pick up stays where you put it. 0 turns this off.", out changed);
        if (changed) _act.SetHold(_dHold);
        if (_dGap != _gapFor) { _gapFor = _dGap; _gapText = _dGap.ToString(System.Globalization.CultureInfo.InvariantCulture) + " ms"; }
        y = SliderRow(dl, x, y, w, "Pause between moves", _gapText, "##gap", ref _dGap, 100, 3000,
            "Auto-sort's pause between two moves, to leave the bot room. Sort now, Clean and Empty don't wait.", out changed);
        if (changed) _act.SetGap(_dGap);
        y = CheckRow(dl, x, y, w, "Sort during fights", "##combat", ref _dCombat, true, "Off: moves wait until the fight is over.", out changed);
        if (changed) _act.SetCombat(_dCombat);
        bool can = _m.Settings.CanOrder;
        y = CheckRow(dl, x, y, w, "Put items in order inside packs", "##order", ref _dOrder, can,
            can ? "Like items side by side, A-Z by name." : "This RynthCore can't read the order inside packs (it needs plugin API 80): items go to their packs but aren't put in order.",
            out changed);
        if (changed) _act.SetOrder(_dOrder);
        y = CheckRow(dl, x, y, w, "Group by kind first", "##kind", ref _dKind, can, "Order by kind, then A-Z by name, instead of A-Z by name only.", out changed);
        if (changed) _act.SetKindFirst(_dKind);
        y = CheckRow(dl, x, y, w, "Keep handy items at the front of the main pack", "##handy", ref _dHandy, can,
            can ? "The things you reach for sit in the main pack's first slots. Items you are using stay where you put them. A kind a pack's rule names goes to that pack instead."
                : "This RynthCore can't move an item to a slot (it needs plugin API 80), so the main pack's order is left as it is.",
            out changed);
        if (changed) _act.SetHandy(_dHandy);
        y = HandyList(dl, x, y, w, can);

        // Empty side packs.
        ImFontPtr lf = Font(RynthUiFont.Ui11), small = Font(RynthUiFont.Ui10);
        float bw = ButtonWidth("Empty all", false);
        y += D(12);
        Text(dl, lf, new Vector2(x, y + (D(34) - Fs(lf)) * 0.5f), Ink, "Empty side packs");
        if (Button("##rs_unpack", "Empty all", null, new Vector2(x + w - bw, y), D(34), Look.Normal, true,
                "Also: /rsort unpack", out _))
        {
            _act.EmptySidePacks();
            _closeSheet = true;
        }
        y += D(34) + D(6);
        y = Wrapped(dl, small, new Vector2(x, y), w, Ink3, "Everything goes to the main pack, and auto-sort turns off so nothing moves back.") + D(18);
        dl.AddLine(new Vector2(x, y), new Vector2(x + w, y), Line);
        y += D(12);
        return Wrapped(dl, small, new Vector2(x, y), w, Ink3,
            "Drag the header to move this window; Ctrl+wheel over it makes it bigger or smaller. Chat: /rsort on | off | now | clean | back | unpack [pack] | add <pack> | status");
    }

    private float SliderRow(ImDrawListPtr dl, float x, float y, float w, string label, string value, string id, ref int v, int min, int max, string help, out bool changed)
    {
        ImFontPtr lf = Font(RynthUiFont.Ui11), vf = Font(RynthUiFont.Dash10), small = Font(RynthUiFont.Ui10);
        y += D(12);
        Text(dl, lf, new Vector2(x, y), Ink, label);
        Text(dl, vf, new Vector2(x + w - Measure(vf, value).X, y + (Fs(lf) - Fs(vf)) * 0.5f), Sky, value);
        y += Fs(lf) + D(8);
        ImGui.SetCursorScreenPos(new Vector2(x, y));
        ImGui.SetNextItemWidth(w);
        int before = v;
        ImGui.SliderInt(id, ref v, min, max, "");
        changed = v != before;
        y += ImGui.GetFrameHeight() + D(6);
        y = Wrapped(dl, small, new Vector2(x, y), w, Ink3, help) + D(12);
        dl.AddLine(new Vector2(x, y), new Vector2(x + w, y), Line);
        return y;
    }

    private float CheckRow(ImDrawListPtr dl, float x, float y, float w, string label, string id, ref bool v, bool enabled, string help, out bool changed)
    {
        ImFontPtr lf = Font(RynthUiFont.Ui11), small = Font(RynthUiFont.Ui10);
        y += D(12);
        float fh = ImGui.GetFrameHeight();
        Text(dl, lf, new Vector2(x, y + (fh - Fs(lf)) * 0.5f), enabled ? Ink : Ink3, label);
        ImGui.SetCursorScreenPos(new Vector2(x + w - fh, y));
        if (!enabled) ImGui.BeginDisabled();
        bool before = v;
        ImGui.Checkbox(id, ref v);
        if (!enabled) ImGui.EndDisabled();
        changed = v != before;
        y += fh + D(6);
        y = Wrapped(dl, small, new Vector2(x, y), w, Ink3, help) + D(12);
        dl.AddLine(new Vector2(x, y), new Vector2(x + w, y), Line);
        return y;
    }

    // ── The kind picker ──────────────────────────────────────────────────

    private void KindsPopup()
    {
        if (!ImGui.IsPopupOpen(KindsId)) return;
        ImGui.SetNextWindowPos(_kindsAnchor, ImGuiCond.Appearing);
        ImGui.SetNextWindowSizeConstraints(new Vector2(D(330), 0), new Vector2(D(440), D(380)));   // room for Overflow and the pack's name
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(D(8), D(8)));
        ImGui.PushStyleVar(ImGuiStyleVar.PopupRounding, D(10));
        bool open = ImGui.BeginPopup(KindsId);
        ImGui.PopStyleVar(2);
        if (!open) return;
        try
        {
            PackModel? pk = _m.Pack(_kindsPack);
            if (pk == null) { ImGui.CloseCurrentPopup(); return; }
            ImFontPtr f = Font(RynthUiFont.Ui11), small = Font(RynthUiFont.Ui10);
            float rowH = Fs(f) + D(10);
            SafeImGui.TextDisabled(_kindsTitle);

            // 0.6.1: a name typed in, no item needed ("Pyreal" sends every item with Pyreal in its name).
            SafeImGui.Text("Items whose name has:");
            float addW = ImGui.CalcTextSize("Add").X + ImGui.GetStyle().FramePadding.X * 2;
            ImGui.SetNextItemWidth(Math.Max(D(120), ImGui.GetContentRegionAvail().X - addW - ImGui.GetStyle().ItemSpacing.X));
            if (ImGui.IsWindowAppearing()) ImGui.SetKeyboardFocusHere();
            bool enter = ImGui.InputTextWithHint("##kname", "Part of a name, like Pyreal or Scarab", ref _kindsText, (uint)Sorting.SortRule.MaxNameLength, ImGuiInputTextFlags.EnterReturnsTrue);
            ImGui.SameLine();
            bool typed = _kindsText.Trim().Length > 0;
            if (!typed) ImGui.BeginDisabled();
            bool add = ImGui.Button("Add##kname");
            if (!typed) ImGui.EndDisabled();
            SafeImGui.SetItemTooltip("Any item with this in its name goes here, whatever its kind (a name beats a kind). It leaves any other pack's rules.");
            if ((enter || add) && typed)
            {
                _act.SendNameText(pk.Number, _kindsText.Trim());
                _kindsText = string.Empty;
                ImGui.CloseCurrentPopup();
                return;
            }
            ImGui.Separator();
            if (_m.CanAddSelected)
            {
                if (ImGui.Selectable("The item selected in the game (by name)", false, ImGuiSelectableFlags.None, new Vector2(0, rowH)))
                    _act.AddSelected(pk.Number);
                SafeImGui.SetItemTooltip(_kindsAddTip);
                ImGui.Separator();
            }
            SafeImGui.Text("Or every item of a kind:");
            float width = Math.Max(D(240), ImGui.GetContentRegionAvail().X);
            for (int i = 0; i < _m.KindChoices.Length; i++)
            {
                KindChoice k = _m.KindChoices[i];
                if (k.InPack == pk.Number) continue;
                ImGui.PushID(i);
                Vector2 p = ImGui.GetCursorScreenPos();
                if (k.InPack > 0) ImGui.SetNextItemAllowOverlap();
                bool picked = ImGui.Selectable("##k", false, ImGuiSelectableFlags.None, new Vector2(0, rowH));
                if (k.InPack != 0)
                    SafeImGui.SetItemTooltip(k.InPack > 0 ? "Its pack now; picking it here moves the kind to this pack (Undo puts it back)." : "Its rule names a pack you don't carry; picking it here sends the kind to this pack.");
                ImDrawListPtr dl = ImGui.GetWindowDrawList();
                dl.AddCircleFilled(new Vector2(p.X + D(8), p.Y + rowH * 0.5f), D(4), Kinds2Abgr(k.Dot), 12);
                Text(dl, f, new Vector2(p.X + D(20), p.Y + (rowH - Fs(f)) * 0.5f), Ink, k.Label);
                bool overflow = false;
                if (k.InPack != 0)
                {
                    string where = k.InPack > 0 ? _m.PackName(k.InPack) : "not carried";
                    float ww = Measure(small, where).X;
                    float wx = p.X + width - ww - D(4);
                    Text(dl, small, new Vector2(wx, p.Y + (rowH - Fs(small)) * 0.5f), Ink3, where);
                    if (k.InPack > 0)
                    {
                        // Keep the kind in its pack; this one takes them when that one is full.
                        float bw = ButtonWidth("Overflow", false) - D(10);
                        overflow = SmallButton("##of", "Overflow", new Vector2(wx - bw - D(8), p.Y + (rowH - D(22)) * 0.5f), new Vector2(bw, D(22)),
                            $"Keep {k.Label.ToLowerInvariant()} in {where}; this pack takes them when {where} is full.");
                    }
                }
                ImGui.PopID();
                if (overflow) { _act.AddOverflow(pk.Number, k.Cls); ImGui.CloseCurrentPopup(); }
                else if (picked) _act.SendKind(pk.Number, k.Cls);
            }
        }
        finally { ImGui.EndPopup(); }
    }
}

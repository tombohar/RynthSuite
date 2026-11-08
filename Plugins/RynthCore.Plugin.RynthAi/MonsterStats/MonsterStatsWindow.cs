// ============================================================================
//  RynthAi - MonsterStats/MonsterStatsWindow.cs
//  The Monster Stats window, the FALLBACK for an engine whose Damage panel
//  doesn't show the numbers under each monster (no GetUiHookFlags bit 8, e.g.
//  plugin API 90): one line per kind of monster fought since login (kills, how
//  often it evades and resists you, how often you evade and resist it), and
//  under each monster, when clicked open, its numbers both ways. RynthCore hosts
//  the window (the sections manifest's "monsterstats" window, plugin API v89:
//  header, pop-out, resize, remembered place) and calls Draw through
//  RynthPluginRenderEmbed. On an engine with the Damage panel's line it isn't
//  declared at all.
//
//  AC's render thread: it reads the view the tick published and queues what it
//  does (Reset) for the tick. Looks: Shared/RynthUi (the player's theme).
// ============================================================================
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using ImGuiNET;
using RynthCore.Plugin.Shared;
using RynthCore.Plugin.Shared.RynthUi;
using RynthCore.PluginSdk;
using static RynthCore.Plugin.Shared.RynthUi.RynthUi;

namespace RynthCore.Plugin.RynthAi.MonsterStats;

/// <summary>What the window shows (immutable; the tick publishes a new one when something changed).</summary>
internal sealed record MonsterStatsView(MonsterStatsRow[] Rows, string FocusName, bool PhysicalCounted, bool InWorld)
{
    public static readonly MonsterStatsView Empty = new(Array.Empty<MonsterStatsRow>(), string.Empty, true, false);
}

internal sealed class MonsterStatsWindow
{
    private readonly Action _reset;
    private readonly HashSet<string> _open = new(StringComparer.OrdinalIgnoreCase);
    private string _filterEdit = string.Empty;   // the box
    private string _filter = string.Empty;       // what it filters by (trimmed)
    private int _sort;                       // 0 recent, 1 name, 2 kills
    private static readonly string[] SortNames = { "Recent", "Name", "Kills" };

    // The shown rows, built again only when the view, the sort or the filter changes.
    private MonsterStatsView? _builtFor;
    private int _builtSort = -1;
    private string _builtFilter = string.Empty;
    private readonly List<MonsterStatsRow> _shown = new();

    private float _k = 1f;
    private float Px(float v) => v * _k;

    private const string HelpText =
        "Your fights since login, one line per kind of monster (every Drudge Skulker together).\n\n" +
        "It evades: of your melee and missile attacks, the share it evaded.\n" +
        "It resists: of your spells that reached it, the share it resisted.\n" +
        "You evade: of its melee and missile attacks, the share you evaded.\n" +
        "You resist: of its spells that reached you, the share you resisted.\n" +
        "XP/kill: the experience a kill gave you, on average (a kill counts when your XP rose within a few seconds of it).\n" +
        "Click a monster for its death too: how long after the kill its corpse came (the death animation; looting waits that long).\n\n" +
        "Click a monster for its numbers both ways: attacks and hits, spells and how many landed, " +
        "spells that could do nothing to it (immune), damage and kills. The killing blow counts as a hit " +
        "(melee or missile) or a landed spell (magic).\n\n" +
        "Melee and missile come from the server's combat events; spells from its spell lines in chat. " +
        "A ring spell counts once for each monster it reaches.\n\n" +
        "The numbers start over with Reset here, /ra mstats reset, the Damage panel's Reset stats, and at " +
        "logout; Reset XP leaves them, and a hot reload keeps them.\n\n" +
        "/ra mstats <name> prints a monster's numbers in chat; /ra mstats show opens this window.";

    public MonsterStatsWindow(Action reset) => _reset = reset;

    /// <summary>Draws the window body into the current ImGui window and returns the size it would like. Render thread.</summary>
    public Vector2 Draw(MonsterStatsView view)
    {
        _k = Math.Max(0.5f, Font(RynthUiFont.Ui11).FontSize / 11f);
        Vector2 origin = ImGui.GetCursorScreenPos();
        Vector2 size = ImGui.GetContentRegionAvail();
        var want = new Vector2(Px(620), Px(360));   // 620 since the XP/kill column (2026-11-08)
        if (size.X < 60 || size.Y < 40) return want;
        ImDrawListPtr frame = ImGui.GetWindowDrawList();
        frame.AddRectFilled(origin, origin + size, Background, 4);
        using (PushFont(RynthUiFont.Ui11))
        {
            ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(4, 4));
            ImGui.SetCursorScreenPos(origin + new Vector2(6, 6));
            ImGui.Indent(6);
            float w = size.X - 12;
            DrawTop(view, w);
            DrawFilter(w);
            Build(view);
            DrawHeader(w);

            // The host's resize grip sits over the bottom-right corner: the rows end above it.
            float bodyH = Math.Max(Px(30), origin.Y + size.Y - 6 - ImGui.GetCursorScreenPos().Y - Px(10));
            ImGui.PushStyleColor(ImGuiCol.ChildBg, Field);
            ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(0, 0));
            ImGui.BeginChild("##ms_rows", new Vector2(w, bodyH));
            DrawRows(view);
            ImGui.EndChild();
            ImGui.PopStyleVar();
            ImGui.PopStyleColor();

            ImGui.Unindent(6);
            ImGui.PopStyleVar();
            DrawResetPopup();
        }
        frame.AddRect(origin, origin + size, Border, 4);
        return want;
    }

    // ── Top line: status, sort, reset, help ─────────────────────────────

    private void DrawTop(MonsterStatsView view, float w)
    {
        Vector2 start = ImGui.GetCursorScreenPos();
        float h = Px(20), iconW = Px(22);
        string sortLabel = "Sort: " + SortNames[_sort];
        float sortW = ButtonWidth(sortLabel), resetW = ButtonWidth("Reset");
        float textW = Math.Max(10, w - sortW - resetW - iconW - Px(12));
        ImDrawListPtr dl = ImGui.GetWindowDrawList();
        string status = !view.InWorld ? "Monster stats start once you are in the world."
            : view.Rows.Length == 0 ? "No fights yet since login."
            : view.Rows.Length == 1 ? "1 kind of monster since login." : $"{view.Rows.Length} kinds of monster since login.";
        dl.PushClipRect(start, start + new Vector2(textW, h), true);
        dl.AddText(new Vector2(start.X, start.Y + (h - ImGui.GetFontSize()) * 0.5f), Mute, status);
        dl.PopClipRect();

        float x = start.X + w - iconW - resetW - sortW - Px(8);
        if (Button("##ms_sort", sortLabel, new Vector2(x, start.Y), new Vector2(sortW, h), Text, ButtonFill))
            _sort = (_sort + 1) % SortNames.Length;
        ImGui.SetItemTooltip("Most recently fought first, by name, or by kills.");
        x += sortW + Px(4);
        if (Button("##ms_reset", "Reset", new Vector2(x, start.Y), new Vector2(resetW, h), Text, ButtonFill, enabled: view.Rows.Length > 0))
            ImGui.OpenPopup("##ms_reset_confirm");
        ImGui.SetItemTooltip("Start every monster's numbers over (the session's XP and kills and the learned damage stay).");
        x += resetW + Px(4);
        IconButton("##ms_help", RynthIcons.Info, new Vector2(x, start.Y), new Vector2(iconW, h), Mute, ButtonFill, font: RynthUiFont.Ui11);
        if (ImGui.IsItemHovered())
        {
            ImGui.BeginTooltip();
            ImGui.PushTextWrapPos(ImGui.GetFontSize() * 32);
            ImGui.TextUnformatted(HelpText);
            ImGui.PopTextWrapPos();
            ImGui.EndTooltip();
        }
        ImGui.SetCursorScreenPos(new Vector2(start.X, start.Y + h + Px(4)));
        if (!view.PhysicalCounted)
        {
            ImGui.PushTextWrapPos(start.X + w);
            Label("RynthCore's server messages are off, so melee and missile attacks aren't counted (spells are). /rc net on turns them on; then reload RynthAi.", Highlight);
            ImGui.PopTextWrapPos();
        }
        ImGui.Dummy(Vector2.Zero);
    }

    private void DrawFilter(float w)
    {
        ImGui.SetNextItemWidth(w);
        ImGui.PushStyleColor(ImGuiCol.FrameBg, Field);
        if (ImGui.InputTextWithHint("##ms_filter", "Find a monster", ref _filterEdit, 64u))
            _filter = _filterEdit.Trim();
        ImGui.PopStyleColor();
    }

    // ── Columns ─────────────────────────────────────────────────────────

    private static readonly (string Head, string Tip)[] Columns =
    {
        ("Kills", "Your kills since login."),
        ("It evades", "Of your melee and missile attacks, the share it evaded (hover a number for the counts)."),
        ("It resists", "Of your spells that reached it, the share it resisted."),
        ("You evade", "Of its melee and missile attacks, the share you evaded."),
        ("You resist", "Of its spells that reached you, the share you resisted."),
        ("XP/kill", "The experience a kill gave, on average (the kills whose XP was seen since login)."),
    };

    private float ColW => Px(66);
    private float CaretW => Px(16);

    /// <summary>Left edge of number column <paramref name="i"/> (0 = Kills), from the row's left edge.</summary>
    private float ColX(float rowW, int i) => rowW - (Columns.Length - i) * ColW;

    private void DrawHeader(float w)
    {
        Vector2 p = ImGui.GetCursorScreenPos();
        float h = Px(18);
        ImDrawListPtr dl = ImGui.GetWindowDrawList();
        dl.AddText(new Vector2(p.X + CaretW, p.Y + (h - ImGui.GetFontSize()) * 0.5f), Mute, "Monster");
        for (int i = 0; i < Columns.Length; i++)
        {
            var at = new Vector2(p.X + ColX(w, i), p.Y);
            ImGui.SetCursorScreenPos(at);
            ImGui.InvisibleButton("##ms_col" + i.ToString(CultureInfo.InvariantCulture), new Vector2(ColW, h));
            if (ImGui.IsItemHovered()) SafeImGui.SetTooltip(Columns[i].Tip);
            float tw = ImGui.CalcTextSize(Columns[i].Head).X;
            dl.AddText(new Vector2(at.X + ColW - tw - Px(6), at.Y + (h - ImGui.GetFontSize()) * 0.5f), Mute, Columns[i].Head);
        }
        ImGui.SetCursorScreenPos(new Vector2(p.X, p.Y + h + Px(2)));
        ImGui.Dummy(Vector2.Zero);
    }

    // ── Rows ────────────────────────────────────────────────────────────

    private void Build(MonsterStatsView view)
    {
        if (ReferenceEquals(view, _builtFor) && _sort == _builtSort && _filter == _builtFilter) return;
        _builtFor = view;
        _builtSort = _sort;
        _builtFilter = _filter;
        _shown.Clear();
        foreach (MonsterStatsRow r in view.Rows)
            if (_filter.Length == 0 || r.Name.IndexOf(_filter, StringComparison.OrdinalIgnoreCase) >= 0)
                _shown.Add(r);
        if (_sort == 1) _shown.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        else if (_sort == 2) _shown.Sort((a, b) => b.Kills != a.Kills ? b.Kills.CompareTo(a.Kills) : b.LastUtcTicks.CompareTo(a.LastUtcTicks));
        // 0: the view's own order, most recently fought first.
    }

    private void DrawRows(MonsterStatsView view)
    {
        float w = ImGui.GetContentRegionAvail().X;
        ImDrawListPtr dl = ImGui.GetWindowDrawList();
        if (_shown.Count == 0)
        {
            ImGui.SetCursorPos(new Vector2(Px(8), Px(8)));
            Label(view.Rows.Length == 0
                ? "Fight something: each monster you fight gets a line here."
                : "No monster's name has \"" + _filter + "\" in it.", Mute);
            return;
        }
        float rowH = Px(22);
        float fontH = ImGui.GetFontSize();
        for (int i = 0; i < _shown.Count; i++)
        {
            MonsterStatsRow r = _shown[i];
            ImGui.PushID(r.Name);
            Vector2 p = ImGui.GetCursorScreenPos();
            bool clicked = ImGui.InvisibleButton("##row", new Vector2(Math.Max(1, w), rowH));
            bool hot = ImGui.IsItemHovered();
            bool open = _open.Contains(r.Name);
            if (clicked)
            {
                if (open) _open.Remove(r.Name); else _open.Add(r.Name);
                open = !open;
            }
            uint fill = hot ? Selected : (i & 1) == 1 ? RowAlt : 0;
            if (fill != 0) dl.AddRectFilled(p, p + new Vector2(w, rowH), fill);
            float ty = p.Y + (rowH - fontH) * 0.5f;
            DrawCentered(dl, Font(RynthUiFont.Ui11), open ? RynthIcons.CaretDown : RynthIcons.CaretRight,
                new Vector2(p.X + Px(2), p.Y), new Vector2(CaretW - Px(2), rowH), Mute);
            bool focus = view.FocusName.Length > 0 && r.Name.Equals(view.FocusName, StringComparison.OrdinalIgnoreCase);
            dl.PushClipRect(new Vector2(p.X + CaretW, p.Y), new Vector2(p.X + ColX(w, 0) - Px(4), p.Y + rowH), true);
            dl.AddText(new Vector2(p.X + CaretW, ty), focus ? Accent : Text, r.Name);
            dl.PopClipRect();

            string[] cells =
            {
                r.Kills.ToString("N0", CultureInfo.InvariantCulture),
                MonsterCombatStats.Percent(r.ItEvadesRate),
                MonsterCombatStats.Percent(r.ItResistsRate),
                MonsterCombatStats.Percent(r.YouEvadeRate),
                MonsterCombatStats.Percent(r.YouResistRate),
                MonsterStatsText.XpCell(r),
            };
            for (int c = 0; c < cells.Length; c++)
            {
                float tw = ImGui.CalcTextSize(cells[c]).X;
                dl.AddText(new Vector2(p.X + ColX(w, c) + ColW - tw - Px(6), ty), cells[c] == "-" ? Faded(Mute) : Text, cells[c]);
            }
            if (hot)
            {
                float mx = ImGui.GetMousePos().X - p.X;
                string? tip = mx >= ColX(w, 1) ? CellTip(r, (int)((mx - ColX(w, 0)) / ColW)) : null;
                SafeImGui.SetTooltip(tip ?? (open ? "Click to fold " + r.Name + " away." : "Click for " + r.Name + "'s numbers both ways."));
            }

            if (open) DrawDetail(r, w);
            ImGui.PopID();
        }
    }

    /// <summary>The counts behind a rate cell (column 1..4).</summary>
    private static string? CellTip(MonsterStatsRow r, int col) => col switch
    {
        1 => $"It evaded {r.Evaded:N0} of your {r.Swings:N0} melee and missile attacks.",
        2 => $"It resisted {r.Resisted:N0} of your {r.SpellsAtIt:N0} spells that reached it.",
        3 => $"You evaded {r.YouEvaded:N0} of its {r.Attacks:N0} melee and missile attacks.",
        4 => $"You resisted {r.YouResisted:N0} of its {r.SpellsAtYou:N0} spells that reached you.",
        5 => r.XpKills > 0 ? $"{r.Xp:N0} XP from {r.XpKills:N0} {(r.XpKills == 1 ? "kill" : "kills")} whose XP was seen." : "No kill's XP seen yet.",
        _ => null,
    };

    /// <summary>One monster's numbers both ways, under its line.</summary>
    private void DrawDetail(MonsterStatsRow r, float w)
    {
        ImGui.Indent(CaretW);
        ImGui.Dummy(new Vector2(0, Px(2)));
        var flags = ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.SizingStretchProp;
        ImGui.PushStyleVar(ImGuiStyleVar.CellPadding, new Vector2(Px(6), Px(2)));
        if (ImGui.BeginTable("##detail", 3, flags, new Vector2(Math.Max(Px(200), w - CaretW - Px(8)), 0)))
        {
            ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthStretch, 0.8f);
            ImGui.TableSetupColumn("You at it", ImGuiTableColumnFlags.WidthStretch, 1.2f);
            ImGui.TableSetupColumn("It at you", ImGuiTableColumnFlags.WidthStretch, 1.2f);
            ImGui.TableHeadersRow();
            foreach (var (label, mine, its) in MonsterStatsText.DetailRows(r))
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                Label(label, Mute);
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(mine);
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(its);
            }
            ImGui.EndTable();
        }
        ImGui.PopStyleVar();
        ImGui.Dummy(new Vector2(0, Px(6)));
        ImGui.Unindent(CaretW);
    }

    private void DrawResetPopup()
    {
        if (!ImGui.BeginPopup("##ms_reset_confirm")) return;
        ImGui.TextUnformatted("Start every monster's numbers over?");
        Label("The session's XP and kills stay.", Mute);
        if (ImGui.Button("Reset")) { _reset(); _open.Clear(); ImGui.CloseCurrentPopup(); }
        ImGui.SameLine();
        if (ImGui.Button("Cancel")) ImGui.CloseCurrentPopup();
        ImGui.EndPopup();
    }
}

/// <summary>The words for one monster's numbers: the window's detail rows and /ra mstats's lines. Pure.</summary>
internal static class MonsterStatsText
{
    private static string N(long v) => v.ToString("N0", CultureInfo.InvariantCulture);

    /// <summary>The detail table: a label, your side, its side.</summary>
    public static List<(string Label, string Mine, string Its)> DetailRows(MonsterStatsRow r)
    {
        var rows = new List<(string, string, string)>
        {
            ("Attacks", $"{N(r.Swings)} swings, {N(r.Hits)} hit", $"{N(r.Attacks)} attacks, {N(r.HitsTaken)} hit"),
            ("Evaded", $"it evaded {N(r.Evaded)} ({MonsterCombatStats.Percent(r.ItEvadesRate)})",
                $"you evaded {N(r.YouEvaded)} ({MonsterCombatStats.Percent(r.YouEvadeRate)})"),
            ("Spells", $"{N(r.SpellsAtIt)} reached it, {N(r.SpellsLanded)} landed", $"{N(r.SpellsAtYou)} reached you, {N(r.SpellsTaken)} landed"),
            ("Resisted", $"it resisted {N(r.Resisted)} ({MonsterCombatStats.Percent(r.ItResistsRate)})",
                $"you resisted {N(r.YouResisted)} ({MonsterCombatStats.Percent(r.YouResistRate)})"),
        };
        if (r.NoEffect > 0 || r.NoEffectOnYou > 0)
            rows.Add(("No effect", $"{N(r.NoEffect)} (it is immune)", $"{N(r.NoEffectOnYou)} (you are immune)"));
        rows.Add(("Damage", $"{N(r.DamageDealt)} dealt", $"{N(r.DamageTaken)} taken"));
        rows.Add(("Kills", N(r.Kills), string.Empty));
        if (r.XpKills > 0) rows.Add(("XP", $"XP/kill {N(XpPerKill(r))} ({N(r.Xp)} from {N(r.XpKills)} {(r.XpKills == 1 ? "kill" : "kills")})", string.Empty));
        if (r.CorpseKills > 0) rows.Add(("Death", $"corpse after {Seconds(r)} ({N(r.CorpseKills)} timed)", string.Empty));
        return rows;
    }

    /// <summary>"2.1 s": the average kill-to-corpse time.</summary>
    private static string Seconds(MonsterStatsRow r)
        => ((r.CorpseAvgMs ?? 0) / 1000.0).ToString("0.0", CultureInfo.InvariantCulture) + " s";

    /// <summary>", corpse after 2.1 s" for the chat lines, or "" before any corpse was timed.</summary>
    private static string CorpseText(MonsterStatsRow r) => r.CorpseKills > 0 ? $", corpse after {Seconds(r)}" : string.Empty;

    /// <summary>The average XP per kill, whole.</summary>
    private static long XpPerKill(MonsterStatsRow r) => r.XpPerKill is double x ? (long)Math.Round(x, MidpointRounding.AwayFromZero) : 0;

    /// <summary>The window's XP/kill cell: "1,234,567", or "-" before any kill's XP was seen.</summary>
    public static string XpCell(MonsterStatsRow r) => r.XpKills > 0 ? N(XpPerKill(r)) : "-";

    /// <summary>", XP/kill 1,234,567" for the chat lines, or "" before any kill's XP was seen.</summary>
    private static string XpText(MonsterStatsRow r) => r.XpKills > 0 ? $", XP/kill {N(XpPerKill(r))}" : string.Empty;

    /// <summary>/ra mstats's answer: a title line, then your side and its side (three chat lines).</summary>
    public static string[] ChatLines(MonsterStatsRow r) => new[]
    {
        $"[RynthAi] {r.Name} since login: {N(r.Kills)} {(r.Kills == 1 ? "kill" : "kills")}"
            + (r.XpKills > 0 ? $"{XpText(r)} ({N(r.Xp)} XP from {N(r.XpKills)})" : string.Empty) + CorpseText(r) + ".",
        $"[RynthAi]   You at it: {N(r.Swings)} swings, {N(r.Hits)} hit, it evaded {N(r.Evaded)} ({MonsterCombatStats.Percent(r.ItEvadesRate)}); "
            + $"{N(r.SpellsAtIt)} spells reached it, it resisted {N(r.Resisted)} ({MonsterCombatStats.Percent(r.ItResistsRate)})"
            + (r.NoEffect > 0 ? $", {N(r.NoEffect)} no effect" : string.Empty)
            + $"; {N(r.DamageDealt)} damage.",
        $"[RynthAi]   It at you: {N(r.Attacks)} attacks, {N(r.HitsTaken)} hit, you evaded {N(r.YouEvaded)} ({MonsterCombatStats.Percent(r.YouEvadeRate)}); "
            + $"{N(r.SpellsAtYou)} spells reached you, you resisted {N(r.YouResisted)} ({MonsterCombatStats.Percent(r.YouResistRate)})"
            + (r.NoEffectOnYou > 0 ? $", {N(r.NoEffectOnYou)} no effect" : string.Empty)
            + $"; {N(r.DamageTaken)} damage.",
    };

    /// <summary>One short line for /ra mstats list.</summary>
    public static string ListLine(MonsterStatsRow r) =>
        $"[RynthAi]   {r.Name}: {N(r.Kills)} {(r.Kills == 1 ? "kill" : "kills")}, it evades {MonsterCombatStats.Percent(r.ItEvadesRate)}, "
        + $"resists {MonsterCombatStats.Percent(r.ItResistsRate)}; you evade {MonsterCombatStats.Percent(r.YouEvadeRate)}, "
        + $"resist {MonsterCombatStats.Percent(r.YouResistRate)}{XpText(r)}{CorpseText(r)}";
}

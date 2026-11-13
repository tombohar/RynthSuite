// ============================================================================
//  RynthAi - ActiveGear/ActiveGearWindow.cs
//  The Active Gear window: one row per worn item with spells (slot, whether its
//  magic is on, its mana, the time its mana lasts, its spells on hover), the
//  summary line, and Check items (identify the worn items again). RynthCore
//  hosts the window (the sections manifest's "activegear" window, plugin API
//  v89: header, pop-out, resize, remembered place; no bar button) and calls
//  Draw through RynthPluginRenderEmbed.
//
//  AC's render thread: it reads the view the tick published (RynthAiPlugin.
//  ActiveGear.cs) and queues Check items for the tick. Looks: Shared/RynthUi.
// ============================================================================
using System;
using System.Globalization;
using System.Numerics;
using System.Text;
using ImGuiNET;
using RynthCore.Plugin.Shared;
using RynthCore.Plugin.Shared.RynthUi;
using RynthCore.PluginSdk;
using static RynthCore.Plugin.Shared.RynthUi.RynthUi;

namespace RynthCore.Plugin.RynthAi.ActiveGear;

/// <summary>One worn item in the view.</summary>
internal sealed record ActiveGearRow(int Id, GearFacts Facts, GearVerdict Verdict, string SlotName, string[] SpellNames);

/// <summary>What the window shows (immutable; the tick publishes a new one about every second while it shows).</summary>
internal sealed record ActiveGearView(ActiveGearRow[] Rows, string Summary, bool InWorld, bool EnchantmentsReadable,
                                      int CheckDone, int CheckTotal, bool CanCheck)
{
    public static readonly ActiveGearView Empty = new(Array.Empty<ActiveGearRow>(), string.Empty, false, false, -1, 0, false);
    /// <summary>Check items is still sending.</summary>
    public bool Checking => CheckDone >= 0;
}

/// <summary>The words for a row: the window's cells and /ra gear list's lines. Pure.</summary>
internal static class ActiveGearText
{
    public static string Mana(ActiveGearRow r) =>
        !r.Facts.Identified ? "?"
        : r.Facts.MaxMana is int max && max > 0
            ? $"{(r.Facts.CurMana is int c ? c.ToString("N0", CultureInfo.InvariantCulture) : "?")} / {max.ToString("N0", CultureInfo.InvariantCulture)}"
            : "-";

    public static string TimeLeft(ActiveGearRow r) =>
        !r.Facts.Identified ? "?" : GearActivation.TimeText(r.Verdict.SecondsLeft);

    public static string Spells(ActiveGearRow r) =>
        !r.Facts.Identified ? "?"
        : r.SpellNames.Length == 0 ? "-"
        : $"{r.Verdict.SpellsOn}/{r.SpellNames.Length} on";

    /// <summary>The hover list: each spell, on or not.</summary>
    public static string SpellList(ActiveGearRow r)
    {
        if (r.SpellNames.Length == 0) return r.Facts.Identified ? "No spells read." : "Not identified yet: Check items reads its spells.";
        var sb = new StringBuilder();
        for (int i = 0; i < r.SpellNames.Length; i++)
        {
            bool on = i < r.Verdict.SpellOn.Count && r.Verdict.SpellOn[i];
            if (i > 0) sb.Append('\n');
            sb.Append(on ? "on   " : "--   ").Append(r.SpellNames[i]);
        }
        return sb.ToString();
    }

    /// <summary>"Left Ring, Ring of X: Yes, mana 1,234 / 4,000, 3 h 5 min left, 2/2 spells on" (/ra gear list).</summary>
    public static string ListLine(ActiveGearRow r)
    {
        var sb = new StringBuilder();
        sb.Append(r.SlotName).Append(", ").Append(r.Facts.Name.Length > 0 ? r.Facts.Name : "(no name)").Append(": ")
          .Append(GearActivation.ActiveText(r.Verdict.Active));
        if (r.Verdict.Reason.Length > 0) sb.Append(" (").Append(r.Verdict.Reason).Append(')');
        if (r.Facts.Identified)
        {
            string mana = Mana(r);
            if (mana != "-") sb.Append(", mana ").Append(mana);
            if (r.Verdict.SecondsLeft is double) sb.Append(", ").Append(TimeLeft(r)).Append(" left");
            if (r.SpellNames.Length > 0) sb.Append(", ").Append(r.Verdict.SpellsOn).Append('/').Append(r.SpellNames.Length).Append(" spells on");
        }
        return sb.ToString();
    }
}

internal sealed class ActiveGearWindow
{
    private readonly Action _check;
    private float _k = 1f;
    private float Px(float v) => v * _k;

    private const string HelpText =
        "Your worn items that have spells, like MagTools' mana tracker.\n\n" +
        "Active: Yes when one of its spells is on you (an item's spell on its wearer never runs out) or on the item " +
        "itself (its appraisal lists that). No when it is out of mana, you lack the Arcane Lore or the skill it needs, " +
        "or a recent appraisal shows none of its spells on. ? when that can't be told yet: not identified, or identified " +
        "before you put it on.\n\n" +
        "Mana and time left come from the item's last appraisal (the server doesn't send mana as it burns): the time " +
        "left is its mana over its mana rate, less the time since it was identified. An estimate.\n\n" +
        "Check items identifies each worn item again, one every 1.5 s, so the reads are fresh.\n\n" +
        "/ra gear prints the summary in chat and opens this window; /ra gear list prints each item.";

    public ActiveGearWindow(Action check) => _check = check;

    /// <summary>Draws the window body into the current ImGui window and returns the size it would like. Render thread.</summary>
    public Vector2 Draw(ActiveGearView view)
    {
        _k = Math.Max(0.5f, Font(RynthUiFont.Ui11).FontSize / 11f);
        Vector2 origin = ImGui.GetCursorScreenPos();
        Vector2 size = ImGui.GetContentRegionAvail();
        var want = new Vector2(Px(640), Px(320));
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
            // The host's resize grip sits over the bottom-right corner: the rows end above it.
            float bodyH = Math.Max(Px(30), origin.Y + size.Y - 6 - ImGui.GetCursorScreenPos().Y - Px(10));
            DrawTable(view, new Vector2(w, bodyH));
            ImGui.Unindent(6);
            ImGui.PopStyleVar();
        }
        frame.AddRect(origin, origin + size, Border, 4);
        return want;
    }

    private void DrawTop(ActiveGearView view, float w)
    {
        Vector2 start = ImGui.GetCursorScreenPos();
        float h = Px(20), iconW = Px(22);
        const string checkLabel = "Check items";
        float checkW = ButtonWidth(checkLabel);
        float textW = Math.Max(10, w - checkW - iconW - Px(10));
        string status = !view.InWorld ? "Active gear shows once you are in the world."
            : view.Summary.Length > 0 ? view.Summary : "Reading your gear...";
        ImGui.PushTextWrapPos(start.X + textW);
        Label(status, view.Rows.Length > 0 ? Text : Mute);
        ImGui.PopTextWrapPos();
        float textBottom = ImGui.GetCursorScreenPos().Y;

        float x = start.X + w - iconW - checkW - Px(4);
        if (Button("##ag_check", checkLabel, new Vector2(x, start.Y), new Vector2(checkW, h), Text, ButtonFill,
                enabled: view.InWorld && view.CanCheck && !view.Checking))
            _check();
        ImGui.SetItemTooltip(view.Checking
            ? $"Identifying: {view.CheckDone} of {view.CheckTotal} asked so far."
            : "Identify each worn item again (one every 1.5 s) so its mana and spells are fresh (/ra gear check).");
        x += checkW + Px(4);
        IconButton("##ag_help", RynthIcons.Info, new Vector2(x, start.Y), new Vector2(iconW, h), Mute, ButtonFill, font: RynthUiFont.Ui11);
        if (ImGui.IsItemHovered())
        {
            ImGui.BeginTooltip();
            ImGui.PushTextWrapPos(ImGui.GetFontSize() * 32);
            ImGui.TextUnformatted(HelpText);
            ImGui.PopTextWrapPos();
            ImGui.EndTooltip();
        }
        ImGui.SetCursorScreenPos(new Vector2(start.X, Math.Max(textBottom, start.Y + h + Px(4))));
        if (view.Checking)
            Label($"Identifying worn items: {view.CheckDone} of {view.CheckTotal} asked...", Mute);
        else if (view.InWorld && !view.EnchantmentsReadable)
            Label("Your enchantments can't be read yet, so only mana and requirements are judged.", Highlight);
        ImGui.Dummy(Vector2.Zero);
    }

    private static readonly (string Head, string Tip)[] Columns =
    {
        ("Item", "The item's name."),
        ("Slot", "Where it is worn."),
        ("Active", "Yes, No, or ? (hover a cell for the reason)."),
        ("Mana", "Its mana and its most, from its last appraisal."),
        ("Time left", "How long its mana lasts at its rate: an estimate from the last appraisal."),
        ("Spells", "Its spells that are on, of all it has (hover for the list)."),
    };

    private void DrawTable(ActiveGearView view, Vector2 size)
    {
        if (view.InWorld && view.Rows.Length == 0)
        {
            Label("No worn items with spells.", Mute);
            return;
        }
        var flags = ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.ScrollY;
        ImGui.PushStyleColor(ImGuiCol.TableRowBgAlt, RowAlt);
        ImGui.PushStyleVar(ImGuiStyleVar.CellPadding, new Vector2(Px(5), Px(2)));
        if (ImGui.BeginTable("##ag_rows", Columns.Length, flags, size))
        {
            ImGui.TableSetupScrollFreeze(0, 1);
            ImGui.TableSetupColumn(Columns[0].Head, ImGuiTableColumnFlags.WidthStretch, 2.4f);
            ImGui.TableSetupColumn(Columns[1].Head, ImGuiTableColumnFlags.WidthStretch, 1.0f);
            ImGui.TableSetupColumn(Columns[2].Head, ImGuiTableColumnFlags.WidthStretch, 0.6f);
            ImGui.TableSetupColumn(Columns[3].Head, ImGuiTableColumnFlags.WidthStretch, 1.1f);
            ImGui.TableSetupColumn(Columns[4].Head, ImGuiTableColumnFlags.WidthStretch, 0.9f);
            ImGui.TableSetupColumn(Columns[5].Head, ImGuiTableColumnFlags.WidthStretch, 0.7f);
            ImGui.TableNextRow(ImGuiTableRowFlags.Headers);
            for (int c = 0; c < Columns.Length; c++)
            {
                ImGui.TableSetColumnIndex(c);
                Label(Columns[c].Head, Mute);
                if (ImGui.IsItemHovered()) SafeImGui.SetTooltip(Columns[c].Tip);
            }
            foreach (ActiveGearRow r in view.Rows)
            {
                ImGui.PushID(r.Id);
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                Label(r.Facts.Name.Length > 0 ? r.Facts.Name : "(no name)", Text);
                if (ImGui.IsItemHovered()) SafeImGui.SetTooltip(RowTip(r));
                ImGui.TableNextColumn();
                Label(r.SlotName, Mute);
                ImGui.TableNextColumn();
                Label(GearActivation.ActiveText(r.Verdict.Active), r.Verdict.Active switch
                {
                    GearActive.Yes => Green,
                    GearActive.No => Red,
                    _ => Highlight,
                });
                if (ImGui.IsItemHovered()) SafeImGui.SetTooltip(RowTip(r));
                ImGui.TableNextColumn();
                Label(ActiveGearText.Mana(r), r.Verdict.ManaPercent is int p && p < 25 ? Highlight : Text);
                ImGui.TableNextColumn();
                string left = ActiveGearText.TimeLeft(r);
                Label(left, left is "-" or "?" ? Faded(Mute) : r.Verdict.SecondsLeft is double s && s < 30 * 60 ? Highlight : Text);
                ImGui.TableNextColumn();
                Label(ActiveGearText.Spells(r), Text);
                if (ImGui.IsItemHovered()) SafeImGui.SetTooltip(ActiveGearText.SpellList(r));
                ImGui.PopID();
            }
            ImGui.EndTable();
        }
        ImGui.PopStyleVar();
        ImGui.PopStyleColor();
    }

    /// <summary>The hover on a row's name and Active cell: the verdict and why, then its spells.</summary>
    private static string RowTip(ActiveGearRow r)
    {
        string head = r.Verdict.Active switch
        {
            GearActive.Yes => "Active",
            GearActive.No => "Not active",
            _ => "Can't tell yet",
        };
        if (r.Verdict.Reason.Length > 0) head += ": " + r.Verdict.Reason;
        return head + "\n\n" + ActiveGearText.SpellList(r);
    }
}

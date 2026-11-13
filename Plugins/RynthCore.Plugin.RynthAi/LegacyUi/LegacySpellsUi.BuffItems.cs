// LegacySpellsUi.BuffItems.cs - the Spells window's right pane as two tabs: Buff Profile (the
// profile editor) and Buff Items: which items buffing may use for their buff (Use Buff Items).
//
// Render thread. The list is the settings' (BlockedBuffItems + CustomBuffItems, replaced, never
// edited in place, so the buff tick never sees a list change under it); the pump thread hands in
// each line's pack count and status every 2 s. Adding an item needs the game (the selection, the
// item's spell), so the two Add buttons send /ra buffitems to the plugin tick.
using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;
using ImGuiNET;
using RynthCore.Plugin.Shared;

namespace RynthCore.Plugin.RynthAi.LegacyUi;

internal sealed unsafe partial class LegacySpellsUi
{
    private static readonly byte[] ProfileTabLabel = Utf8Z("Buff Profile##sptabprofile");
    private static readonly byte[] BuffItemsTabLabel = Utf8Z("Buff Items##sptabitems");

    private Action<string>? _buffItemsCommand;   // queues "/ra buffitems <args>" for the plugin tick
    private List<BuffItemRow>? _buffItemRows;     // pump thread writes (Volatile)
    private int _selectBuffItemsTab;
    private string _newBuffItemName = "";
    private sealed record BuffItemsStatus(string Text, bool Warning);
    private BuffItemsStatus? _buffItemsStatus;     // pump thread or render thread writes (Volatile)

    private static byte[] Utf8Z(string s)
    {
        var b = new byte[System.Text.Encoding.UTF8.GetByteCount(s) + 1];
        System.Text.Encoding.UTF8.GetBytes(s, 0, s.Length, b, 0);
        return b;
    }

    /// <summary>The Add buttons' way to the plugin tick (arguments of /ra buffitems).</summary>
    public void SetBuffItemsCommand(Action<string> command) => _buffItemsCommand = command;

    /// <summary>Pump thread: each line's pack count and status.</summary>
    public void SetBuffItemRows(List<BuffItemRow> rows) => Volatile.Write(ref _buffItemRows, rows);

    /// <summary>Any thread: the line under the Add buttons (what the last add did).</summary>
    public void SetBuffItemsStatus(string text, bool warning) => Volatile.Write(ref _buffItemsStatus, new BuffItemsStatus(text, warning));

    /// <summary>Any thread: the next frame shows the Buff Items tab (/ra buffitems open).</summary>
    public void ShowBuffItemsTab() => Interlocked.Exchange(ref _selectBuffItemsTab, 1);

    /// <summary>BeginTabItem with flags and no close button (ImGui.NET pairs flags with p_open, which draws an X).</summary>
    private static bool BeginTab(byte[] labelUtf8Z, ImGuiTabItemFlags flags)
    {
        fixed (byte* label = labelUtf8Z)
            return ImGuiNative.igBeginTabItem(label, null, flags) != 0;
    }

    private void RenderRightTabs()
    {
        if (!ImGui.BeginTabBar("##spright")) return;
        if (BeginTab(ProfileTabLabel, ImGuiTabItemFlags.None))
        {
            RenderProfileEditor();
            ImGui.EndTabItem();
        }
        var select = Interlocked.Exchange(ref _selectBuffItemsTab, 0) == 1 ? ImGuiTabItemFlags.SetSelected : ImGuiTabItemFlags.None;
        if (BeginTab(BuffItemsTabLabel, select))
        {
            RenderBuffItems();
            ImGui.EndTabItem();
        }
        ImGui.EndTabBar();
    }

    private static BuffItemRow? RowFor(List<BuffItemRow>? rows, string name)
    {
        if (rows == null) return null;
        foreach (var r in rows)
            if (r.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) return r;
        return null;
    }

    private void RenderBuffItems()
    {
        ImGui.Checkbox("Use Buff Items##spusebuffitems", ref _settings.UseBuffItems);
        if (ImGui.IsItemHovered())
            SafeImGui.SetTooltip("Use the ticked items below from your pack whenever their buff isn't on you.\n" +
                                 "The same switch as Settings > Buffing > Use Buff Items.");
        ImGui.PushTextWrapPos(0);
        ImGui.TextDisabled("One item at a time, when its buff isn't on you. Untick an item to keep RynthAi from using it. " +
                           "Items used up on use (potions) are used only from your pack.");
        ImGui.PopTextWrapPos();

        var entries = BuffItemEntry.Build(_settings.BlockedBuffItems, _settings.CustomBuffItems);
        var rows = Volatile.Read(ref _buffItemRows);
        string? remove = null;

        float listH = MathF.Max(90f, ImGui.GetContentRegionAvail().Y - ImGui.GetFrameHeightWithSpacing() * 4.6f);
        const ImGuiTableFlags flags = ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.BordersInnerV
            | ImGuiTableFlags.Resizable | ImGuiTableFlags.SizingFixedFit;
        if (ImGui.BeginTable("##spbuffitems", 6, flags, new Vector2(0, listH)))
        {
            ImGui.TableSetupScrollFreeze(0, 1);
            ImGui.TableSetupColumn("Allowed", ImGuiTableColumnFlags.WidthFixed, 52);
            ImGui.TableSetupColumn("Item", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("Pack", ImGuiTableColumnFlags.WidthFixed, 36);
            ImGui.TableSetupColumn("Status", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("Every (min)", ImGuiTableColumnFlags.WidthFixed, 78);
            ImGui.TableSetupColumn("##remove", ImGuiTableColumnFlags.WidthFixed, 56);
            ImGui.TableHeadersRow();

            for (int i = 0; i < entries.Count; i++)
            {
                var e = entries[i];
                var row = RowFor(rows, e.Name);
                ImGui.TableNextRow();
                ImGui.PushID("bi" + i);

                ImGui.TableSetColumnIndex(0);
                bool allowed = e.Allowed;
                if (ImGui.Checkbox("##allowed", ref allowed))
                {
                    _settings.SetBuffItemAllowed(e.Name, allowed, out _);
                    e.Allowed = allowed;
                }
                if (ImGui.IsItemHovered()) ImGui.SetTooltip(allowed ? "Allowed: RynthAi may use it." : "Blocked: RynthAi never uses it.");

                ImGui.TableSetColumnIndex(1);
                ImGui.BeginGroup();
                if (e.Allowed) ImGui.TextUnformatted(e.Name);
                else SafeImGui.TextDisabled(e.Name);
                if (e.BuiltIn) { ImGui.SameLine(); ImGui.TextDisabled("(built in)"); }
                ImGui.EndGroup();
                if (ImGui.IsItemHovered()) BuffItemTooltip(e, row);

                ImGui.TableSetColumnIndex(2);
                if (row == null) ImGui.TextDisabled("?");
                else if (row.InPack > 0) ImGui.TextUnformatted(row.InPack.ToString());
                else ImGui.TextDisabled("0");

                ImGui.TableSetColumnIndex(3);
                string status = row?.Status ?? (e.Allowed ? "" : "blocked");
                if (status.StartsWith("buff on", StringComparison.Ordinal)) SafeImGui.TextColored(GoodColor, status);
                else if (status.StartsWith("its buff didn't show", StringComparison.Ordinal)) SafeImGui.TextColored(WarnColor, status);
                else SafeImGui.TextDisabled(status);

                ImGui.TableSetColumnIndex(4);
                if (e.Custom != null && !(e.SpellsKnown && e.Spells.Length > 0))
                {
                    int minutes = e.RepeatMinutes;
                    ImGui.SetNextItemWidth(-1);
                    if (ImGui.InputInt("##repeat", ref minutes, 0, 0))
                        e.Custom.RepeatMinutes = BuffItemRules.ClampRepeatMinutes(minutes);
                    if (ImGui.IsItemHovered())
                        SafeImGui.SetTooltip(e.SpellsKnown
                            ? "This item casts no spell RynthAi can check, so it is used\nagain this many minutes after its last use."
                            : "Used only if the item turns out to cast no spell RynthAi can check:\nthen it is used again this many minutes after its last use.");
                }
                else ImGui.TextDisabled("-");

                ImGui.TableSetColumnIndex(5);
                if (e.Custom != null && ImGui.SmallButton("Remove")) remove = e.Name;

                ImGui.PopID();
            }
            ImGui.EndTable();
        }

        if (remove != null)
        {
            string name = remove;
            Confirm($"Remove {name} from the buff items?", () =>
            {
                _settings.RemoveCustomBuffItem(name, out string shown);
                SetBuffItemsStatus($"Removed {shown}.", false);
            });
        }

        ImGui.Separator();
        ImGui.TextUnformatted("Add your own item");
        if (ImGui.Button("Add selected item##spbiaddsel"))
        {
            _buffItemsCommand?.Invoke("addselected");
            SetBuffItemsStatus("Adding the selected item...", false);
        }
        if (ImGui.IsItemHovered())
            SafeImGui.SetTooltip("Select the item in your inventory, then click. RynthAi reads the spell it casts,\n" +
                                 "so it knows when its buff is on. One with no spell it can read is used on a timer.");
        ImGui.SetNextItemWidth(MathF.Max(120f, ImGui.GetContentRegionAvail().X - 70f));
        bool enter = ImGui.InputTextWithHint("##spbiname", "Item name, exactly as in your pack", ref _newBuffItemName, BuffItemRules.MaxNameLength,
            ImGuiInputTextFlags.EnterReturnsTrue);
        ImGui.SameLine();
        bool add = ImGui.Button("Add##spbiadd");
        if ((enter || add) && _newBuffItemName.Trim().Length > 0)
        {
            _buffItemsCommand?.Invoke("add " + BuffItemRules.CleanName(_newBuffItemName));
            SetBuffItemsStatus($"Adding {BuffItemRules.CleanName(_newBuffItemName)}...", false);
            _newBuffItemName = "";
        }

        var st = Volatile.Read(ref _buffItemsStatus);
        if (st != null && st.Text.Length > 0)
        {
            ImGui.PushTextWrapPos(0);
            if (st.Warning) SafeImGui.TextColored(WarnColor, st.Text);
            else SafeImGui.TextDisabled(st.Text);
            ImGui.PopTextWrapPos();
        }
    }

    private static void BuffItemTooltip(BuffItemEntry e, BuffItemRow? row)
    {
        ImGui.BeginTooltip();
        ImGui.PushTextWrapPos(ImGui.GetFontSize() * 30f);
        ImGui.TextUnformatted(e.Name + (e.BuiltIn ? " (built in)" : ""));
        if (e.Spells.Length > 0)
            ImGui.TextUnformatted("Its buff counts as on while this is on you: " + (row != null && row.Spells.Length > 0 ? row.Spells : string.Join(", ", e.Spells)));
        else if (e.SpellsKnown)
            ImGui.TextUnformatted($"It casts no spell RynthAi can check: used every {e.RepeatMinutes} min.");
        else
            ImGui.TextUnformatted("Its spell is read from the item when it is in your pack, before the first use.");
        if (e.BuiltIn) ImGui.TextUnformatted("Built-in items can be blocked but not removed.");
        ImGui.PopTextWrapPos();
        ImGui.EndTooltip();
    }
}

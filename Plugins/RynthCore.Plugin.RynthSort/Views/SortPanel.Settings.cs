using RynthCore.Plugin.RynthSort.Sorting;
using RynthCore.Plugin.RynthSort.Ui;

namespace RynthCore.Plugin.RynthSort.Views;

// The Settings page.
internal sealed partial class SortPanel
{
    private void DrawSettingsTab(UiWindow w)
    {
        SortSettings s = _c.Data.Settings;
        w.SeparatorText("When items move");
        w.Checkbox("Sort during fights", "combat", s.SortInCombat, v => { s.SortInCombat = v; _save(); });
        w.Tooltip("Off: nothing moves while RynthAi fights. On: moves go between its attacks and casts (never while one is unanswered)");
        w.SliderInt("Settle (s)", "settle", s.SettleSec, SortSettings.SettleMin, SortSettings.SettleMax, v => { s.SettleSec = v; _save(); });
        w.Tooltip("How long a new item sits in the main pack before auto-sort moves it");
        w.SliderInt("Leave alone (min)", "hold", s.HoldMin, SortSettings.HoldMinMin, SortSettings.HoldMinMax, v => { s.HoldMin = v; _save(); });
        w.Tooltip("Items you use stay where they are this long: ones you move, select, drag, take off or pick up. 0 turns it off.");
        w.PushStyleColor(UiFormat.ColText, UiColors.Grey);
        w.TextWrapped(s.HoldMin == 0
            ? "Off: an item you take out of its pack goes back at the next sort."
            : $"An item you move, select, drag, take off or pick up stays put for {Count(s.HoldMin, "minute")} (Empty side packs and Put back still move it).");
        w.PopStyleColor();
        w.SliderInt("Gap (ms)", "gap", s.GapMs, SortSettings.GapMin, SortSettings.GapMax, v => { s.GapMs = v; _save(); });
        w.Tooltip("Auto-sort's pause between two moves, to leave the bot room. Sort now, Clean and Empty don't wait.");

        w.SeparatorText("Inside packs");
        w.Checkbox("Put items in order inside packs", "order", s.OrderInPacks, v => { s.OrderInPacks = v; _save(); });
        w.Tooltip("Like items side by side, A-Z by name, in every pack. Off: items only go to their packs (and stacks are still joined).");
        w.Checkbox("Group by kind first", "orderkind", s.OrderKindFirst, v => { s.OrderKindFirst = v; _save(); });
        w.Tooltip("Order by kind (Armor, Gem, Spell Component ...) and then A-Z by name, instead of A-Z by name only");
        if (!(_c.Abilities.ReadOrder && _c.Abilities.PlaceMoves))
            w.TextWrapped("This RynthCore can't read the order inside packs (it needs plugin API 80): items go to their packs but aren't put in order.");

        w.SeparatorText("Tools");
        w.Button("Empty side packs", "job.unpack", () => _c.StartUnpack(null));
        w.Tooltip("Move everything from your side packs into the main pack. Turns auto-sort off first, so nothing moves back. Also: /rsort unpack");
        w.TextDisabled("Chat: /rsort on | off | now | clean | back | unpack [pack] | add <pack> | status");
    }
}

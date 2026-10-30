using System;
using System.Collections.Generic;
using RynthCore.Plugin.RynthDefective.Items;
using RynthCore.Plugin.RynthDefective.Model;
using RynthCore.Plugin.RynthDefective.Ui;
using RynthCore.Plugin.RynthDefective.Use;

namespace RynthCore.Plugin.RynthDefective.Views;

/// <summary>
/// The item bar window: a grid of slot buttons (the item's picture, its count or uses left, its key
/// under it), a row with Edit, Settings and Weapons, and the status line. Edit opens the slot editor
/// below (set a slot from the item selected in the game, pick one from the pack, how it is used,
/// its key); Settings the sizes and switches. Recorded on the pump thread, replayed by the engine.
/// </summary>
internal sealed class BarView
{
    /// <summary>Logical units a button needs besides its picture: the frame padding and the gap to the next.</summary>
    public const float CellExtra = 16f;

    private readonly DefSession _s;
    private readonly SlotEditor _editor;
    private readonly SettingsPanel _settings;

    public bool EditOpen, SettingsOpen;

    /// <summary>The window should be sized to its content (a setting changed, a section opened).</summary>
    public Action RequestFit = () => { };
    /// <summary>Show the weapon picker window.</summary>
    public Action ShowPicker = () => { };

    public BarView(DefSession session)
    {
        _s = session;
        _editor = new SlotEditor(session);
        _settings = new SettingsPanel(session, () => RequestFit());
    }

    public SlotEditor Editor => _editor;

    /// <summary>The window size that fits the grid (and the sections that are open), in logical units.</summary>
    public (float W, float H) WantedSize()
    {
        DefSettings st = _s.Settings;
        int perRow = Math.Min(st.SlotsPerRow, st.SlotCount);
        int rows = (st.SlotCount + st.SlotsPerRow - 1) / st.SlotsPerRow;
        float cell = st.ButtonSize + CellExtra;
        float w = Math.Max(250f, perRow * cell + 28f);
        float h = rows * (st.ButtonSize + (st.ShowCounts ? 50f : 14f)) + 70f;
        if (EditOpen) { h += 330f; w = Math.Max(w, 330f); }
        if (SettingsOpen) h += 230f;
        return (w, h);
    }

    public void Draw(UiWindow w)
    {
        if (!_s.LoggedIn)
        {
            w.TextDisabled("Log in to use the item bar.");
            return;
        }
        IReadOnlyList<CarriedItem> items = _s.Items();
        DrawGrid(w, items);
        DrawToolbar(w);
        DrawStatus(w);
        if (EditOpen)
        {
            w.Separator();
            _editor.Draw(w, items);
        }
        if (SettingsOpen)
        {
            w.Separator();
            _settings.Draw(w);
        }
    }

    // ── The grid ────────────────────────────────────────────────────────────

    private void DrawGrid(UiWindow w, IReadOnlyList<CarriedItem> items)
    {
        DefSettings st = _s.Settings;
        float size = st.ButtonSize;
        float pitch = size + CellExtra;
        var labels = new List<(string Text, uint Color)>();
        var keys = new List<string>();
        for (int rowStart = 0; rowStart < st.SlotCount; rowStart += st.SlotsPerRow)
        {
            int end = Math.Min(st.SlotCount, rowStart + st.SlotsPerRow);
            labels.Clear();
            keys.Clear();
            for (int i = rowStart; i < end; i++)
            {
                if (i > rowStart) w.SameLine();
                DrawSlotButton(w, i + 1, items, size, out string text, out uint color, out string key);
                labels.Add((text, color));
                keys.Add(key);
            }
            if (!st.ShowCounts) continue;
            for (int j = 0; j < labels.Count; j++)
            {
                if (j > 0) w.SameLine(j * pitch);
                w.TextColored(labels[j].Color, labels[j].Text.Length > 0 ? labels[j].Text : " ");
            }
            for (int j = 0; j < keys.Count; j++)
            {
                if (j > 0) w.SameLine(j * pitch);
                w.TextColored(SlotText.KeyColor, keys[j].Length > 0 ? keys[j] : " ");
            }
        }
    }

    private void DrawSlotButton(UiWindow w, int number, IReadOnlyList<CarriedItem> items, float size,
        out string text, out uint color, out string key)
    {
        DefSlot slot = _s.Data.Slots[number - 1];
        string id = "slot" + number;
        string? keyFull = _s.KeyOfSlot(number);
        key = SlotText.ShortKey(keyFull);
        text = string.Empty;
        color = SlotText.Plain;
        bool icons = _s.Settings.ShowIcons && _s.IconsAvailable;

        if (slot.IsSpell)
        {
            // A spell slot (set with /rdef spell or on the Shortcuts bar): its picture, or its initials.
            string spell = slot.SpellLabel;
            SpellInfo? info = SpellTable.TryGet(slot.SpellId, out SpellInfo si) ? si : null;
            bool casting = _s.Use.InvolvesSpell(slot.SpellId);
            if (icons) w.ImageButton(UiFormat.IconKindSpell, slot.SpellId, id, () => Press(number), size, size, 0xFFFFFFFFu, casting ? SlotText.Waiting : 0u);
            else
            {
                if (casting) w.PushStyleColor(UiFormat.ColButton, SlotText.Waiting);
                w.Button(SlotText.Abbreviation(spell), id, () => Press(number), size + 8f, size + 8f);
                if (casting) w.PopStyleColor();
            }
            w.Tooltip($"Slot {number}: {spell}, a spell.\nClick or press the key: {SpellTargeting.Describe(info)} (Magic mode first)."
                      + (keyFull != null ? "\nKey: " + keyFull : "\nNo key yet (Edit slots)."));
            return;
        }

        if (slot.Item == null)
        {
            w.Button("+", id, () => Choose(number), size + 8f, size + 8f);
            w.Tooltip(SlotText.Tooltip(number, null, default, slot.Mode, keyFull));
            return;
        }

        SlotStatus s = ItemMatcher.Status(slot.Item, items);
        text = SlotText.Count(s, out color);
        string tip = SlotText.Tooltip(number, slot.Item, s, slot.Mode, keyFull);
        bool busy = _s.Use.Involves(slot.Item);
        bool inHand = s.Item is { Wielded: true };
        uint? fill = busy ? SlotText.Waiting : inHand ? UiFormat.Rgb(40, 110, 40) : null;

        if (icons && s.Item != null)
        {
            w.ImageButton(UiFormat.IconKindObject, s.Item.Id, id, () => Press(number), size, size, 0xFFFFFFFFu, fill ?? 0u);
        }
        else if (icons && slot.Item.Icon != 0)
        {
            // Not carried: the last picture seen, dimmed.
            w.ImageButton(UiFormat.IconKindIcon, slot.Item.Icon, id, () => Press(number), size, size, UiFormat.Rgb(255, 255, 255, 90));
        }
        else
        {
            if (fill != null) w.PushStyleColor(UiFormat.ColButton, fill.Value);
            else if (s.Item == null) w.PushStyleColor(UiFormat.ColText, SlotText.Missing);
            w.Button(SlotText.Abbreviation(slot.Item.FullName), id, () => Press(number), size + 8f, size + 8f);
            if (fill != null) w.PopStyleColor();
            else if (s.Item == null) w.PopStyleColor();
        }
        w.Tooltip(tip);
    }

    private void Press(int number)
    {
        string? problem = _s.UseSlot(number);
        if (problem != null) _s.SetStatus(problem, warning: true);
    }

    /// <summary>An empty slot's button: open the editor on it.</summary>
    private void Choose(int number)
    {
        if (!EditOpen) { EditOpen = true; RequestFit(); }
        _editor.Select(number);
    }

    // ── Toolbar and status ──────────────────────────────────────────────────

    private void DrawToolbar(UiWindow w)
    {
        w.ToggleButton("Edit slots", "tb.edit", EditOpen, () => { EditOpen = !EditOpen; RequestFit(); }, small: true);
        w.SameLine();
        w.ToggleButton("Settings", "tb.settings", SettingsOpen, () => { SettingsOpen = !SettingsOpen; RequestFit(); }, small: true);
        w.SameLine();
        w.SmallButton("Weapons", "tb.weapons", () => ShowPicker());
        w.Tooltip("The weapon picker: every weapon and wand you carry, one click to wield (/rdef weapons)");
    }

    private void DrawStatus(UiWindow w)
    {
        UseRequest? pending = _s.Use.Pending;
        if (pending != null)
        {
            w.TextColored(SlotText.Waiting, $"Waiting to use {pending.Label}...");
            return;
        }
        string status = _s.Status(out bool warning);
        if (status.Length > 0) w.TextColored(warning ? SlotText.Missing : SlotText.Plain, status);
        else w.TextDisabled("Click a slot, or press its key.");
    }
}

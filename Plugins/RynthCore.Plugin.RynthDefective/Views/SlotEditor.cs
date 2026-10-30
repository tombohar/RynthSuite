using System;
using System.Collections.Generic;
using RynthCore.Plugin.RynthDefective.Items;
using RynthCore.Plugin.RynthDefective.Model;
using RynthCore.Plugin.RynthDefective.Ui;

namespace RynthCore.Plugin.RynthDefective.Views;

/// <summary>
/// The slot editor under the bar: every slot as a row (picture, name, key), and for the one picked
/// its buttons - Set from selected (the item selected in the game goes in the slot), Pick from pack
/// (a searchable list of what is carried), Clear, move up and down - how pressing it uses the item,
/// and its key (typed here, bound through the engine's hotkeys as "/rdef use N" or by the item's name).
/// </summary>
internal sealed class SlotEditor
{
    private const float RowIcon = 22f;
    private const int PackRows = 40;

    private readonly DefSession _s;
    private int _selected = 1;
    private bool _picking;
    private string _packSearch = string.Empty;
    private string _keyDraft = string.Empty;
    private string _note = string.Empty;

    public SlotEditor(DefSession session) => _s = session;

    public int SelectedSlot => _selected;
    public bool Picking => _picking;
    public string Note => _note;

    public void Select(int number)
    {
        _selected = Math.Clamp(number, 1, DefSettings.SlotsMax);
        _picking = false;
        _keyDraft = string.Empty;
        _note = string.Empty;
    }

    private void Say(string text)
    {
        _note = text;
        _s.SetStatus(text);
    }

    public void Draw(UiWindow w, IReadOnlyList<CarriedItem> items)
    {
        DefSettings st = _s.Settings;
        if (_selected > st.SlotCount) _selected = st.SlotCount;
        bool icons = st.ShowIcons && _s.IconsAvailable;

        w.TextDisabled("Pick a slot, then set it from the item selected in the game or from your pack.");
        w.BeginChild("slotlist", 0f, Math.Min(st.SlotCount, 6) * (RowIcon + 6f) + 8f, true);
        for (int n = 1; n <= st.SlotCount; n++)
        {
            DefSlot slot = _s.Data.Slots[n - 1];
            int number = n;
            string? key = _s.KeyOfSlot(n);
            string name = slot.Empty ? "(empty)" : slot.Label;
            if (icons)
            {
                CarriedItem? have = slot.Item == null ? null : ItemMatcher.Find(slot.Item, items, out _);
                if (slot.IsSpell) w.Image(UiFormat.IconKindSpell, slot.SpellId, RowIcon, RowIcon);
                else if (have != null) w.Image(UiFormat.IconKindObject, have.Id, RowIcon, RowIcon);
                else if (slot.Item != null && slot.Item.Icon != 0) w.Image(UiFormat.IconKindIcon, slot.Item.Icon, RowIcon, RowIcon, UiFormat.Rgb(255, 255, 255, 90));
                else w.Dummy(RowIcon, RowIcon);
                w.SameLine();
            }
            string label = $"{n}.  {name}" + (key != null ? $"   [{key}]" : string.Empty);
            w.Selectable(label, "row" + n, _selected == n, () => Select(number), 0f, icons ? RowIcon : 0f);
        }
        w.EndChild();

        DrawSlot(w, items);
    }

    private void DrawSlot(UiWindow w, IReadOnlyList<CarriedItem> items)
    {
        int n = _selected;
        DefSlot slot = _s.Data.Slots[n - 1];
        w.TextColored(UiColors.Gold, $"Slot {n}: " + (slot.Empty ? "(empty)" : slot.Label));

        w.Button("Set from selected", "ed.set", () => Say(_s.SetFromSelected(n)));
        w.Tooltip("Select an item in the game (click it in your pack), then press this: it goes in this slot.");
        w.SameLine();
        w.ToggleButton("Pick from pack", "ed.pick", _picking, () => { _picking = !_picking; _packSearch = string.Empty; });
        w.Tooltip("Choose from a list of what you carry.");
        w.SameLine();
        w.Button("Clear", "ed.clear", () => Say(_s.ClearSlot(n)));

        w.SmallButton("Move up", "ed.up", () => { if (_s.SwapSlots(n, n - 1)) _selected = n - 1; });
        w.SameLine();
        w.SmallButton("Move down", "ed.down", () => { if (_s.SwapSlots(n, n + 1)) _selected = n + 1; });

        uint sel = _s.SelectedId();
        CarriedItem? selItem = sel == 0 ? null : _s.FindById(sel);
        w.TextDisabled("Selected in the game: " + (sel == 0 ? "nothing" : selItem != null ? selItem.FullName : "(not in your pack)"));
        if (_note.Length > 0) w.TextWrapped(_note);

        if (_picking) DrawPackPicker(w, items, n);

        if (slot.IsSpell)
        {
            SpellInfo? info = SpellTable.TryGet(slot.SpellId, out SpellInfo si) ? si : null;
            w.TextDisabled($"A spell: pressing the slot has it {SpellTargeting.Describe(info)} (Magic mode first). /rdef spell {n} <name> puts another in.");
            DrawKey(w, n, slot);
            return;
        }
        if (slot.Item == null) return;

        int mode = (int)slot.Mode;
        w.Combo("Pressing it", "ed.mode", mode, UseModes.Labels, i => _s.SetSlotMode(n, (UseMode)Math.Clamp(i, 0, (int)UseMode.Equip)));
        SlotStatus status = ItemMatcher.Status(slot.Item, items);
        if (status.Item != null)
        {
            ItemKind kind = ItemKinds.Classify(status.Item);
            UseAction action = UseModes.ActionFor(slot.Mode, kind);
            w.TextDisabled($"It is {Describe(kind)}: pressing the slot has it {UseModes.Describe(action)}.");
        }
        else
        {
            w.TextColored(SlotText.Missing, "Not in your pack right now. The slot keeps the name and fills itself when you carry another.");
        }

        DrawKey(w, n, slot);
    }

    private void DrawKey(UiWindow w, int n, DefSlot slot)
    {
        string? key = _s.KeyOfSlot(n);
        w.Text("Key: " + (key ?? "none"));
        w.InputText("Key to bind", "ed.key", _keyDraft, 24, v => _keyDraft = v, UiFormat.InputEnterReturnsTrue);
        w.Tooltip("A key with Ctrl, Alt or Shift if you like: F9, Ctrl+1, Alt+Shift+Num3.");
        w.Button("Bind to this slot", "ed.bind", () => Say(_s.BindKey(n, _keyDraft, byName: false)));
        w.Tooltip($"Runs {DefCommandParser.UseCommand(n)}. If you move or re-set the slot, the key follows the slot.");
        w.SameLine();
        w.Button("Bind to this item", "ed.bindname", () => Say(_s.BindKey(n, _keyDraft, byName: true)));
        w.Tooltip("Runs /rdef use <the item's name>, whichever slot (or none) holds it. Works for every character on this PC.");
        w.SameLine();
        w.Button("Unbind", "ed.unbind", () => Say(_s.UnbindKey(n)));
        w.TextDisabled("Any item can be bound by name from Settings > Hotkeys too: /rdef use <name>.");
    }

    private void DrawPackPicker(UiWindow w, IReadOnlyList<CarriedItem> items, int n)
    {
        bool icons = _s.Settings.ShowIcons && _s.IconsAvailable;
        w.InputText("Search your pack", "ed.packsearch", _packSearch, 48, v => _packSearch = v);
        List<PackChoice> choices = PackList.Build(items, _packSearch, PackRows, out int total);
        w.BeginChild("packlist", 0f, 150f, true);
        foreach (PackChoice c in choices)
        {
            CarriedItem it = c.Item;
            if (icons) { w.Image(UiFormat.IconKindObject, it.Id, RowIcon, RowIcon); w.SameLine(); }
            string label = it.FullName + (c.Count > 1 ? $"  x{c.Count}" : string.Empty);
            w.Selectable(label, "pk" + it.Id, false, () =>
            {
                Say(_s.SetSlot(n, it));
                _picking = false;
            }, 0f, icons ? RowIcon : 0f);
        }
        if (choices.Count == 0) w.TextDisabled("Nothing matches.");
        if (total > choices.Count) w.TextDisabled($"{total - choices.Count} more - type to narrow the list.");
        w.EndChild();
    }

    private static string Describe(ItemKind k) => k switch
    {
        ItemKind.Kit => "a healing or stamina kit",
        ItemKind.Melee => "a melee weapon",
        ItemKind.Missile => "a missile weapon",
        ItemKind.WarWand => "a war magic wand",
        ItemKind.VoidWand => "a void magic wand",
        ItemKind.Shield => "a shield",
        ItemKind.Armor => "armor or clothing",
        ItemKind.Jewelry => "jewelry",
        ItemKind.Ammo => "ammunition",
        _ => "an item",
    };
}

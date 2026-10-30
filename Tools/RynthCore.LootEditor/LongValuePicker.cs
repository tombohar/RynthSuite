using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using RynthCore.Loot.Editing;

namespace RynthCore.LootEditor;

/// <summary>
/// The value cell of a long-key condition (value on line 0, key on line 1). When
/// the key has named values (LootRuleText.ValueTableFor: WieldSkilltype's skills,
/// MaterialType's materials...) it is a picker showing "Light Weapons (45)" plus a
/// number box for any value; a value not in the list shows as "value N". Other
/// keys get the plain number box. Follows the key: picking another key swaps the
/// cell. The line still holds the plain number, so the .utl format is unchanged.
/// </summary>
internal sealed class LongValuePicker : ContentControl
{
    private readonly VTankConditionVm _vm;
    private readonly Action _onChange;
    private readonly bool _integer;
    private string? _builtForKey;
    private ComboBox? _combo;
    private TextBox? _box;
    private (int Id, string Name)[]? _table;
    private bool _syncing;

    private sealed record Opt(int? Id, string Display);

    /// <summary>The value cell for <paramref name="vm"/>; <paramref name="integer"/> false keeps decimals (buffed long).</summary>
    public static Control For(VTankConditionVm vm, Action onChange, bool integer) => new LongValuePicker(vm, onChange, integer);

    private LongValuePicker(VTankConditionVm vm, Action onChange, bool integer)
    {
        _vm = vm;
        _onChange = onChange;
        _integer = integer;
        var d = _vm.Data.DataLines;
        while (d.Count < 2) d.Add("0");
        Rebuild();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _vm.PropertyChanged += OnVmChanged;
        if (_builtForKey != KeyLine) Rebuild();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _vm.PropertyChanged -= OnVmChanged;   // the condition vm outlives this cell
        base.OnDetachedFromVisualTree(e);
    }

    private string KeyLine => _vm.Data.DataLines.Count > 1 ? _vm.Data.DataLines[1] : string.Empty;
    private string ValueLine => _vm.Data.DataLines.Count > 0 ? _vm.Data.DataLines[0] : string.Empty;

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_builtForKey != KeyLine) Rebuild();   // the key combo picked another key
    }

    private void Rebuild()
    {
        _builtForKey = KeyLine;
        _table = LootRuleText.TryParseValue(KeyLine, out int key) ? LootRuleText.ValueTableFor(key) : null;

        _box = new TextBox { Text = ValueLine, Watermark = "number" };
        _box.LostFocus += (_, _) => CommitBox();
        if (_table == null)
        {
            _combo = null;
            Content = _box;
            return;
        }

        _box.Width = 80;
        _box.Margin = new Thickness(4, 0, 0, 0);
        ToolTip.SetTip(_box, "Any number, named or not");
        _combo = new ComboBox
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            MinWidth = 160,
            IsTextSearchEnabled = true,   // type while open to jump (Olthoi, Virindi, …)
            MaxDropDownHeight = 360,
        };
        _combo.DisplayMemberBinding = new Avalonia.Data.Binding(nameof(Opt.Display));
        if (LootRuleText.ValueTableIsFlags(key))
            ToolTip.SetTip(_combo, "Flags: other combinations are typed as a number in the box");
        else if (key == 166)
            ToolTip.SetTip(_combo, "Slayer creature type — pick by name (list is A–Z). Any slayer: use >= 1");
        else
            ToolTip.SetTip(_combo, "Pick by name, or type a number in the box");
        _combo.SelectionChanged += (_, _) =>
        {
            if (_syncing || _combo.SelectedItem is not Opt { Id: int id }) return;
            Write(id.ToString(CultureInfo.InvariantCulture), syncCombo: false);   // no list reset inside its own event
        };
        SyncCombo();

        var grid = new Grid { ColumnDefinitions = ColumnDefinitions.Parse("*,Auto") };
        Grid.SetColumn(_combo, 0);
        Grid.SetColumn(_box, 1);
        grid.Children.Add(_combo);
        grid.Children.Add(_box);
        Content = grid;
    }

    /// <summary>Fills the picker and selects the current value; one not in the list gets a "value N" entry on top.</summary>
    private void SyncCombo()
    {
        if (_combo == null || _table == null) return;
        var items = new List<Opt>(_table.Length + 1);
        Opt? selected = null;
        bool isNumber = LootRuleText.TryParseValue(ValueLine, out int current);
        foreach (var (id, name) in _table)
        {
            var o = new Opt(id, LootRuleText.PickerLabel(id, name));
            if (isNumber && id == current && selected == null) selected = o;
            items.Add(o);
        }
        if (selected == null)
        {
            selected = new Opt(null, "value " + ValueLine.Trim());
            items.Insert(0, selected);
        }
        _syncing = true;
        try
        {
            _combo.ItemsSource = items;
            _combo.SelectedItem = selected;
        }
        finally { _syncing = false; }
    }

    private void CommitBox()
    {
        if (_box == null) return;
        string text = (_box.Text ?? string.Empty).Trim();
        if (_integer && int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v))
            text = v.ToString(CultureInfo.InvariantCulture);
        else if (!_integer && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double dv))
            text = dv.ToString(CultureInfo.InvariantCulture);
        else
        {
            _box.Text = ValueLine;   // not a number: put the old value back
            return;
        }
        Write(text, syncCombo: true);
    }

    private void Write(string text, bool syncCombo)
    {
        var d = _vm.Data.DataLines;
        if (_box != null && _box.Text != text) _box.Text = text;
        if (d[0] == text) return;
        d[0] = text;
        if (syncCombo) SyncCombo();
        _vm.NotifyDataChanged();
        _onChange();
    }
}

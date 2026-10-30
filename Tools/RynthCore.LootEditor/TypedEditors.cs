using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using System;
using System.Collections.Generic;
using System.Globalization;

namespace RynthCore.LootEditor;

/// <summary>
/// Per-NodeType typed editors. Each builder reads/writes the underlying
/// VTankConditionVm.Data.DataLines so changes round-trip through the writer.
/// </summary>
internal static class TypedEditors
{
    private static readonly IBrush MutedBrush = new SolidColorBrush(Color.Parse("#7A8896"));

    // ── ObjectClass ──────────────────────────────────────────────────────────

    // Decal/VTank numbering (AcObjectClass), the same table the evaluator and the
    // in-game ImGui editor use. The old hand-written list drifted from id 11 on.
    public static readonly (int Id, string Name)[] ObjectClasses =
        RynthCore.Loot.Editing.LootRuleText.ObjectClasses;

    public static (int Id, string Name)[] ObjectClassesAccess() => ObjectClasses;

    public static Control ObjectClassEditor(VTankConditionVm vm, Action onChange)
    {
        return Row("Object Class",
            Combo(ObjectClasses, vm.Data.DataLines, 0, onChange, vm));
    }

    // ── Long key conditions (LE/GE/E/NE/Flag) ────────────────────────────────

    public static Control LongKeyEditor(VTankConditionVm vm, Action onChange, string op)
    {
        var grid = new Grid
        {
            ColumnDefinitions = ColumnDefinitions.Parse("Auto,*,Auto,Auto,*"),
            RowDefinitions = RowDefinitions.Parse("Auto")
        };
        AddCell(grid, Label("Key"),                                col: 0);
        AddCell(grid, KeyCombo(LongKeys, vm.Data.DataLines, 1, onChange, vm), col: 1);
        AddCell(grid, Label(op),                                   col: 2);
        AddCell(grid, Label("Value"),                              col: 3);
        AddCell(grid, LongValuePicker.For(vm, onChange, integer: true), col: 4);
        return grid;
    }

    public static Control LongFlagEditor(VTankConditionVm vm, Action onChange)
    {
        var grid = new Grid
        {
            ColumnDefinitions = ColumnDefinitions.Parse("Auto,*,Auto,Auto,*"),
            RowDefinitions = RowDefinitions.Parse("Auto")
        };
        AddCell(grid, Label("Key"),                                col: 0);
        AddCell(grid, KeyCombo(LongKeys, vm.Data.DataLines, 1, onChange, vm), col: 1);
        AddCell(grid, Label("has flag"),                           col: 2);
        AddCell(grid, Label("Value"),                              col: 3);
        AddCell(grid, IntBox(vm.Data.DataLines, 0, onChange, vm),  col: 4);
        return grid;
    }

    public static Control DoubleKeyEditor(VTankConditionVm vm, Action onChange, string op)
    {
        var grid = new Grid
        {
            ColumnDefinitions = ColumnDefinitions.Parse("Auto,*,Auto,Auto,*"),
            RowDefinitions = RowDefinitions.Parse("Auto")
        };
        AddCell(grid, Label("Key"),                                  col: 0);
        AddCell(grid, KeyCombo(DoubleKeys, vm.Data.DataLines, 1, onChange, vm), col: 1);
        AddCell(grid, Label(op),                                     col: 2);
        AddCell(grid, Label("Value"),                                col: 3);
        AddCell(grid, DoubleBox(vm.Data.DataLines, 0, onChange, vm), col: 4);
        return grid;
    }

    public static Control DoubleKeyAsLongEditor(VTankConditionVm vm, Action onChange, string op)
    {
        // Buffed-long: the value is stored as a double in slot 0 even though the key indexes a long property.
        var grid = new Grid
        {
            ColumnDefinitions = ColumnDefinitions.Parse("Auto,*,Auto,Auto,*"),
            RowDefinitions = RowDefinitions.Parse("Auto")
        };
        AddCell(grid, Label("Key (long)"),                            col: 0);
        AddCell(grid, KeyCombo(LongKeys, vm.Data.DataLines, 1, onChange, vm), col: 1);
        AddCell(grid, Label(op),                                      col: 2);
        AddCell(grid, Label("Value"),                                 col: 3);
        AddCell(grid, LongValuePicker.For(vm, onChange, integer: false), col: 4);
        return grid;
    }

    // ── String / spell ───────────────────────────────────────────────────────

    public static Control StringValueEditor(VTankConditionVm vm, Action onChange)
    {
        var grid = new Grid
        {
            ColumnDefinitions = ColumnDefinitions.Parse("Auto,*,Auto,*"),
            RowDefinitions = RowDefinitions.Parse("Auto")
        };
        AddCell(grid, Label("String key"),                             col: 0);
        AddCell(grid, KeyCombo(StringKeys, vm.Data.DataLines, 1, onChange, vm), col: 1);
        AddCell(grid, Label("matches"),                                 col: 2);
        AddCell(grid, StringBox(vm.Data.DataLines, 0, onChange, vm, "regex pattern"), col: 3);
        return grid;
    }

    public static Control SingleStringEditor(VTankConditionVm vm, Action onChange, string label)
    {
        return Row(label, StringBox(vm.Data.DataLines, 0, onChange, vm, label));
    }

    public static Control SpellMatchEditor(VTankConditionVm vm, Action onChange)
    {
        var grid = new Grid
        {
            ColumnDefinitions = ColumnDefinitions.Parse("Auto,*"),
            RowDefinitions = RowDefinitions.Parse("Auto,Auto,Auto"),
        };
        AddCell(grid, Label("Match regex"),                           col: 0, row: 0);
        AddCell(grid, StringBox(vm.Data.DataLines, 0, onChange, vm),  col: 1, row: 0);
        AddCell(grid, Label("No-match regex"),                        col: 0, row: 1);
        AddCell(grid, StringBox(vm.Data.DataLines, 1, onChange, vm),  col: 1, row: 1);
        AddCell(grid, Label("Min count"),                             col: 0, row: 2);
        AddCell(grid, IntBox(vm.Data.DataLines, 2, onChange, vm),     col: 1, row: 2);
        return grid;
    }

    // ── Single-value editors ────────────────────────────────────────────────

    public static Control SingleIntEditor(VTankConditionVm vm, Action onChange, string label)
        => Row(label, IntBox(vm.Data.DataLines, 0, onChange, vm));

    public static Control SingleDoubleEditor(VTankConditionVm vm, Action onChange, string label)
        => Row(label, DoubleBox(vm.Data.DataLines, 0, onChange, vm));

    public static Control TwoIntEditor(VTankConditionVm vm, Action onChange, string a, string b)
    {
        var grid = new Grid
        {
            ColumnDefinitions = ColumnDefinitions.Parse("Auto,*,Auto,*"),
            RowDefinitions = RowDefinitions.Parse("Auto"),
        };
        AddCell(grid, Label(a),                                   col: 0);
        AddCell(grid, IntBox(vm.Data.DataLines, 0, onChange, vm), col: 1);
        AddCell(grid, Label(b),                                   col: 2);
        AddCell(grid, IntBox(vm.Data.DataLines, 1, onChange, vm), col: 3);
        return grid;
    }

    public static Control ThreeDoubleEditor(VTankConditionVm vm, Action onChange, string a, string b, string c)
    {
        var grid = new Grid
        {
            ColumnDefinitions = ColumnDefinitions.Parse("Auto,*"),
            RowDefinitions = RowDefinitions.Parse("Auto,Auto,Auto"),
        };
        AddCell(grid, Label(a), col: 0, row: 0); AddCell(grid, DoubleBox(vm.Data.DataLines, 0, onChange, vm), col: 1, row: 0);
        AddCell(grid, Label(b), col: 0, row: 1); AddCell(grid, DoubleBox(vm.Data.DataLines, 1, onChange, vm), col: 1, row: 1);
        AddCell(grid, Label(c), col: 0, row: 2); AddCell(grid, DoubleBox(vm.Data.DataLines, 2, onChange, vm), col: 1, row: 2);
        return grid;
    }

    // ── Skill ────────────────────────────────────────────────────────────────

    public static Control SkillGEEditor(VTankConditionVm vm, Action onChange)
    {
        var grid = new Grid
        {
            ColumnDefinitions = ColumnDefinitions.Parse("Auto,*,Auto,Auto,*"),
            RowDefinitions = RowDefinitions.Parse("Auto"),
        };
        AddCell(grid, Label("Skill"),                                col: 0);
        AddCell(grid, KeyCombo(Skills, vm.Data.DataLines, 1, onChange, vm), col: 1);
        AddCell(grid, Label("≥"),                                    col: 2);
        AddCell(grid, Label("Value"),                                col: 3);
        AddCell(grid, IntBox(vm.Data.DataLines, 0, onChange, vm),    col: 4);
        return grid;
    }

    public static Control BaseSkillEditor(VTankConditionVm vm, Action onChange)
    {
        var grid = new Grid
        {
            ColumnDefinitions = ColumnDefinitions.Parse("Auto,*,Auto,*,Auto,*"),
            RowDefinitions = RowDefinitions.Parse("Auto"),
        };
        AddCell(grid, Label("Skill"),                                col: 0);
        AddCell(grid, KeyCombo(Skills, vm.Data.DataLines, 0, onChange, vm), col: 1);
        AddCell(grid, Label("Min"),                                  col: 2);
        AddCell(grid, IntBox(vm.Data.DataLines, 1, onChange, vm),    col: 3);
        AddCell(grid, Label("Max"),                                  col: 4);
        AddCell(grid, IntBox(vm.Data.DataLines, 2, onChange, vm),    col: 5);
        return grid;
    }

    // ── Color ────────────────────────────────────────────────────────────────

    public static Control ColorEditor(VTankConditionVm vm, Action onChange, bool hasArmorOrSlot, string trailingLabel = "")
    {
        var grid = new Grid
        {
            ColumnDefinitions = ColumnDefinitions.Parse("Auto,*"),
            RowDefinitions = RowDefinitions.Parse("Auto,Auto,Auto,Auto,Auto,Auto"),
        };
        AddCell(grid, Label("R"), col: 0, row: 0); AddCell(grid, IntBox(vm.Data.DataLines, 0, onChange, vm), col: 1, row: 0);
        AddCell(grid, Label("G"), col: 0, row: 1); AddCell(grid, IntBox(vm.Data.DataLines, 1, onChange, vm), col: 1, row: 1);
        AddCell(grid, Label("B"), col: 0, row: 2); AddCell(grid, IntBox(vm.Data.DataLines, 2, onChange, vm), col: 1, row: 2);
        AddCell(grid, Label("Max ΔH"), col: 0, row: 3); AddCell(grid, IntBox(vm.Data.DataLines, 3, onChange, vm), col: 1, row: 3);
        AddCell(grid, Label("Max ΔSV"), col: 0, row: 4); AddCell(grid, IntBox(vm.Data.DataLines, 4, onChange, vm), col: 1, row: 4);
        if (hasArmorOrSlot)
        {
            AddCell(grid, Label(trailingLabel), col: 0, row: 5);
            AddCell(grid, IntBox(vm.Data.DataLines, 5, onChange, vm), col: 1, row: 5);
        }
        return grid;
    }

    // ── DisabledRule ─────────────────────────────────────────────────────────

    public static Control DisabledRuleEditor(VTankConditionVm vm, Action onChange)
    {
        EnsureLine(vm, 0, "false");
        var cb = new CheckBox
        {
            Content = "Rule disabled",
            IsChecked = string.Equals(vm.Data.DataLines[0].Trim(), "true", StringComparison.OrdinalIgnoreCase),
        };
        cb.IsCheckedChanged += (_, _) =>
        {
            string newValue = cb.IsChecked == true ? "true" : "false";
            if (vm.Data.DataLines[0] != newValue)
            {
                vm.Data.DataLines[0] = newValue;
                vm.NotifyDataChanged();
                onChange();
            }
        };
        return cb;
    }

    // ── Building blocks ──────────────────────────────────────────────────────

    private static Control Row(string label, Control editor)
    {
        var grid = new Grid
        {
            ColumnDefinitions = ColumnDefinitions.Parse("Auto,*"),
            RowDefinitions = RowDefinitions.Parse("Auto"),
        };
        AddCell(grid, Label(label), col: 0);
        AddCell(grid, editor, col: 1);
        return grid;
    }

    private static TextBlock Label(string text) => new()
    {
        Text = text,
        Foreground = MutedBrush,
        VerticalAlignment = VerticalAlignment.Center,
    };

    private static void AddCell(Grid grid, Control c, int col, int row = 0)
    {
        Grid.SetColumn(c, col);
        Grid.SetRow(c, row);
        grid.Children.Add(c);
    }

    private static TextBox IntBox(IList<string> lines, int idx, Action onChange, VTankConditionVm vm)
    {
        EnsureLine(vm, idx, "0");
        var box = new TextBox { Text = lines[idx] };
        box.LostFocus += (_, _) => Commit(box, lines, idx, vm, onChange, parseInt: true);
        return box;
    }

    private static TextBox DoubleBox(IList<string> lines, int idx, Action onChange, VTankConditionVm vm)
    {
        EnsureLine(vm, idx, "0");
        var box = new TextBox { Text = lines[idx] };
        box.LostFocus += (_, _) => Commit(box, lines, idx, vm, onChange, parseInt: false);
        return box;
    }

    private static TextBox StringBox(IList<string> lines, int idx, Action onChange, VTankConditionVm vm, string? watermark = null)
    {
        EnsureLine(vm, idx, string.Empty);
        var box = new TextBox { Text = lines[idx], Watermark = watermark };
        box.LostFocus += (_, _) =>
        {
            if (lines[idx] != box.Text)
            {
                lines[idx] = box.Text ?? string.Empty;
                vm.NotifyDataChanged();
                onChange();
            }
        };
        return box;
    }

    private static ComboBox KeyCombo((int Id, string Name)[] options, IList<string> lines, int idx, Action onChange, VTankConditionVm vm)
    {
        EnsureLine(vm, idx, "0");
        var combo = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        var items = new List<KvOption>();
        foreach (var (id, name) in options) items.Add(new KvOption(id, $"{id} — {name}"));
        combo.ItemsSource = items;
        combo.DisplayMemberBinding = new Avalonia.Data.Binding(nameof(KvOption.Display));
        int.TryParse(lines[idx].Trim(), out int currentId);
        foreach (var i in items) if (i.Id == currentId) { combo.SelectedItem = i; break; }
        combo.SelectionChanged += (_, _) =>
        {
            if (combo.SelectedItem is KvOption opt && lines[idx] != opt.Id.ToString(CultureInfo.InvariantCulture))
            {
                lines[idx] = opt.Id.ToString(CultureInfo.InvariantCulture);
                vm.NotifyDataChanged();
                onChange();
            }
        };
        return combo;
    }

    private static ComboBox Combo((int Id, string Name)[] options, IList<string> lines, int idx, Action onChange, VTankConditionVm vm)
        => KeyCombo(options, lines, idx, onChange, vm);

    private static void Commit(TextBox box, IList<string> lines, int idx, VTankConditionVm vm, Action onChange, bool parseInt)
    {
        string text = box.Text ?? "0";
        if (parseInt)
        {
            if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v))
                text = v.ToString(CultureInfo.InvariantCulture);
            else { box.Text = lines[idx]; return; }
        }
        else
        {
            if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double d))
                text = d.ToString(CultureInfo.InvariantCulture);
            else { box.Text = lines[idx]; return; }
        }
        if (lines[idx] != text)
        {
            lines[idx] = text;
            vm.NotifyDataChanged();
            onChange();
        }
    }

    private static void EnsureLine(VTankConditionVm vm, int idx, string defaultValue)
    {
        while (vm.Data.DataLines.Count <= idx) vm.Data.DataLines.Add(defaultValue);
    }

    private sealed record KvOption(int Id, string Display);

    // ── Reference key tables (subset of AC enums; covers what people usually edit) ──

    // The LootSdk tables the in-game editor uses (adds 45 DamageType, 48 WeaponSkill).
    public static readonly (int Id, string Name)[] LongKeys =
        RynthCore.Loot.Editing.LootRuleText.LongKeys;

    public static readonly (int Id, string Name)[] DoubleKeys =
        RynthCore.Loot.Editing.LootRuleText.DoubleKeys;

    public static readonly (int Id, string Name)[] StringKeys =
    {
        (1, "Name"),
        (5, "Inscription"),
        (7, "Title"),
    };

    // Retail/ACE skill ids from the LootSdk; the old list here was off from id 19 up
    // (Light Weapons at 47 is Missile Weapons).
    public static readonly (int Id, string Name)[] Skills =
        RynthCore.Loot.Editing.LootRuleText.Skills;
}

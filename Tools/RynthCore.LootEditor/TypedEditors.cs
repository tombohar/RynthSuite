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

    public static readonly (int Id, string Name)[] ObjectClasses =
    {
        (0, "Unknown"),  (1, "MeleeWeapon"), (2, "Armor"),    (3, "Clothing"),
        (4, "Jewelry"),  (5, "Monster"),     (6, "Food"),     (7, "Money"),
        (8, "Misc"),     (9, "MissileWeapon"), (10, "Container"), (11, "Useless"),
        (12, "Gem"),     (13, "SpellComp"),  (14, "Key"),     (15, "WandStaffOrb"),
        (16, "Portal"),  (17, "TradeNote"),  (18, "ManaStone"), (19, "Plant"),
        (20, "BaseAlchemy"), (21, "BaseCooking"), (22, "BaseFletching"), (23, "Player"),
        (24, "Vendor"),  (25, "Door"),      (26, "Corpse"),   (27, "Lifestone"),
        (28, "Foci"),    (29, "Salvage"),   (30, "Ust"),      (31, "Bundle"),
        (32, "Book"),    (33, "Journal"),   (34, "Sign"),     (35, "Housing"),
        (36, "Pack"),    (37, "Scroll"),    (38, "BaseArmorTinker"), (39, "BaseWeaponTinker"),
        (40, "BaseMagicTinker"), (41, "BaseItemTinker"), (50, "Salvage (legacy)"),
    };

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
        AddCell(grid, IntBox(vm.Data.DataLines, 0, onChange, vm),  col: 4);
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
        AddCell(grid, DoubleBox(vm.Data.DataLines, 0, onChange, vm),  col: 4);
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

    public static readonly (int Id, string Name)[] LongKeys =
    {
        (5,   "EncumbVal"),
        (19,  "Value"),
        (25,  "Level"),
        (28,  "ArmorLevel"),
        (54,  "MaxDamage"),
        (87,  "MaxStructure"),
        (88,  "Structure"),
        (105, "ItemWorkmanship"),
        (107, "ItemMaxMana"),
        (131, "MaterialType"),
        (158, "WieldRequirements"),
        (159, "WieldSkilltype"),
        (160, "WieldDifficulty"),
        (218, "EquippedSlots"),
        (353, "ImbuedEffect"),
        (370, "DamageRating"),
        (371, "DamageResistRating"),
        (372, "CritRating"),
        (373, "CritResistRating"),
        (374, "CritDamageRating"),
        (375, "CritDamageResistRating"),
        (376, "HealBoostRating"),
        (379, "VitalityRating"),
    };

    public static readonly (int Id, string Name)[] DoubleKeys =
    {
        (5,   "ApproachDistance"),
        (22,  "DamageVariance"),
        (29,  "WeaponLength"),
        (62,  "WeaponDefense"),
        (63,  "WeaponOffense"),
        (152, "ElementalDamageVsMonsters"),
        (167, "ManaRate"),
    };

    public static readonly (int Id, string Name)[] StringKeys =
    {
        (1, "Name"),
        (5, "Inscription"),
        (7, "Title"),
    };

    public static readonly (int Id, string Name)[] Skills =
    {
        (1,  "Axe"),         (2,  "Bow"),         (3,  "Crossbow"),    (4,  "Dagger"),
        (5,  "Mace"),        (6,  "MeleeDefense"),(7,  "MissileDef."), (8,  "Sling"),
        (9,  "Spear"),       (10, "Staff"),       (11, "Sword"),       (12, "ThrownWpn"),
        (13, "Unarmed"),     (14, "ArcaneLore"),  (15, "MagicDefense"),(16, "ManaConv."),
        (18, "Item Tinkering"), (19, "Healing"),  (20, "Deception"),   (21, "Leadership"),
        (22, "Lockpick"),    (23, "Fletching"),   (24, "Alchemy"),     (25, "Cooking"),
        (26, "Salvaging"),   (31, "Creature Ench."), (32, "Item Ench."), (33, "Life Magic"),
        (34, "War Magic"),   (35, "Loyalty"),     (36, "Jump"),        (37, "Run"),
        (38, "Assess"),      (39, "Weapon Tinkering"), (40, "Armor Tinkering"),
        (41, "Magic Item Tinkering"), (43, "Two Handed"), (44, "Gearcraft"),
        (45, "Void Magic"),  (46, "Heavy Weapons"), (47, "Light Weapons"),
        (48, "Finesse Weapons"), (49, "Missile Weapons"), (50, "Shield"),
        (54, "Dual Wield"),  (55, "Recklessness"), (56, "Sneak Attack"),
    };
}

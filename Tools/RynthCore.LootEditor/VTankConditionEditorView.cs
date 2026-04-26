using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using RynthCore.Loot.VTank;
using System;
using System.Collections.Generic;

namespace RynthCore.LootEditor;

/// <summary>
/// Editor surface for one VTank loot condition. Composed at runtime per
/// condition: a node-type picker on top, then either a typed editor (if we
/// have one for the selected NodeType) or a generic raw-lines editor that
/// guarantees round-trip safety for any node type.
/// </summary>
public sealed class VTankConditionEditorView : ContentControl
{
    private readonly VTankConditionVm _vm;
    private readonly Action _onChange;
    private readonly Brush _muted = new SolidColorBrush(Color.Parse("#7A8896"));

    public VTankConditionEditorView(VTankConditionVm vm, Action onChange)
    {
        _vm = vm;
        _onChange = onChange;
        Build();
    }

    private void Build()
    {
        var stack = new StackPanel { Spacing = 6 };

        // ── Node-type picker row ─────────────────────────────────────────────
        var typeRow = new Grid
        {
            ColumnDefinitions = ColumnDefinitions.Parse("Auto,*"),
        };
        var typeLabel = new TextBlock
        {
            Text = "Node Type",
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = _muted,
        };
        Grid.SetColumn(typeLabel, 0);
        typeRow.Children.Add(typeLabel);

        var typeCombo = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        var options = new List<NodeTypeOption>();
        foreach (int t in VTankNodeTypes.All)
            options.Add(new NodeTypeOption(t, $"{VTankNodeTypes.DisplayName(t)}  (id {t})"));
        typeCombo.ItemsSource = options;
        typeCombo.DisplayMemberBinding = new Avalonia.Data.Binding(nameof(NodeTypeOption.Display));
        typeCombo.SelectedItem = FindOption(options, _vm.NodeType);
        typeCombo.SelectionChanged += (_, _) =>
        {
            if (typeCombo.SelectedItem is NodeTypeOption opt && opt.Id != _vm.NodeType)
            {
                _vm.NodeType = opt.Id;
                EnsureDataLineCount(opt.Id);
                _vm.NotifyDataChanged();
                _onChange();
                // Re-build to swap the typed editor underneath.
                Build();
            }
        };
        Grid.SetColumn(typeCombo, 1);
        typeRow.Children.Add(typeCombo);
        stack.Children.Add(typeRow);

        // ── Data lines editor ────────────────────────────────────────────────
        EnsureDataLineCount(_vm.NodeType);
        var typed = TryBuildTypedEditor(_vm, _onChange);
        if (typed != null)
        {
            stack.Children.Add(typed);
            stack.Children.Add(BuildRawHint());
        }
        else
        {
            stack.Children.Add(BuildRawEditor());
        }

        Content = stack;
    }

    private static NodeTypeOption? FindOption(List<NodeTypeOption> options, int id)
    {
        foreach (var o in options) if (o.Id == id) return o;
        return null;
    }

    /// <summary>
    /// Pads or trims the condition's data lines to match the documented arity
    /// for its node type. New lines default to "0".
    /// </summary>
    private void EnsureDataLineCount(int nodeType)
    {
        int n = VTankNodeTypes.GetDataLineCount(nodeType);
        if (n < 0) return; // unknown — leave as-is so unparsed bytes survive
        var d = _vm.Data.DataLines;
        while (d.Count < n) d.Add("0");
        while (d.Count > n) d.RemoveAt(d.Count - 1);
    }

    private Control BuildRawEditor()
    {
        var panel = new StackPanel { Spacing = 4 };
        panel.Children.Add(new TextBlock
        {
            Text = "Data lines (one per row, in VTank's order)",
            Foreground = _muted,
            FontSize = 11,
        });

        for (int i = 0; i < _vm.Data.DataLines.Count; i++)
        {
            int idx = i;
            var box = new TextBox { Text = _vm.Data.DataLines[idx], AcceptsReturn = false };
            box.LostFocus += (_, _) =>
            {
                if (_vm.Data.DataLines[idx] != box.Text)
                {
                    _vm.Data.DataLines[idx] = box.Text ?? string.Empty;
                    _vm.NotifyDataChanged();
                    _onChange();
                }
            };
            panel.Children.Add(box);
        }

        // Buttons to grow/shrink the data list — only meaningful for unknown
        // node types where we can't infer the arity.
        if (VTankNodeTypes.GetDataLineCount(_vm.NodeType) < 0)
        {
            var btnRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
            var add = new Button { Content = "+ line" };
            add.Click += (_, _) => { _vm.Data.DataLines.Add(""); _onChange(); Build(); };
            var del = new Button { Content = "− line" };
            del.Click += (_, _) =>
            {
                if (_vm.Data.DataLines.Count > 0)
                { _vm.Data.DataLines.RemoveAt(_vm.Data.DataLines.Count - 1); _onChange(); Build(); }
            };
            btnRow.Children.Add(add); btnRow.Children.Add(del);
            panel.Children.Add(btnRow);
        }

        return panel;
    }

    private Control BuildRawHint()
    {
        return new TextBlock
        {
            Text = "Raw values are stored verbatim; round-trip is preserved.",
            Foreground = _muted,
            FontSize = 11,
            Margin = new Thickness(0, 6, 0, 0),
        };
    }

    // ── Typed editors for known node types ───────────────────────────────────

    private static Control? TryBuildTypedEditor(VTankConditionVm vm, Action onChange) => vm.NodeType switch
    {
        VTankNodeTypes.ObjectClass                  => TypedEditors.ObjectClassEditor(vm, onChange),
        VTankNodeTypes.LongValKeyGE                 => TypedEditors.LongKeyEditor(vm, onChange, "≥"),
        VTankNodeTypes.LongValKeyLE                 => TypedEditors.LongKeyEditor(vm, onChange, "≤"),
        VTankNodeTypes.LongValKeyE                  => TypedEditors.LongKeyEditor(vm, onChange, "="),
        VTankNodeTypes.LongValKeyNE                 => TypedEditors.LongKeyEditor(vm, onChange, "≠"),
        VTankNodeTypes.LongValKeyFlagExists         => TypedEditors.LongFlagEditor(vm, onChange),
        VTankNodeTypes.DoubleValKeyGE               => TypedEditors.DoubleKeyEditor(vm, onChange, "≥"),
        VTankNodeTypes.DoubleValKeyLE               => TypedEditors.DoubleKeyEditor(vm, onChange, "≤"),
        VTankNodeTypes.BuffedLongValKeyGE           => TypedEditors.DoubleKeyAsLongEditor(vm, onChange, "≥ (buffed long)"),
        VTankNodeTypes.BuffedDoubleValKeyGE         => TypedEditors.DoubleKeyEditor(vm, onChange, "≥ (buffed double)"),
        VTankNodeTypes.StringValueMatch             => TypedEditors.StringValueEditor(vm, onChange),
        VTankNodeTypes.SpellNameMatch               => TypedEditors.SingleStringEditor(vm, onChange, "Pattern"),
        VTankNodeTypes.SpellCountGE                 => TypedEditors.SingleIntEditor(vm, onChange, "Min count"),
        VTankNodeTypes.SpellMatch                   => TypedEditors.SpellMatchEditor(vm, onChange),
        VTankNodeTypes.MinDamageGE                  => TypedEditors.SingleDoubleEditor(vm, onChange, "Min damage ≥"),
        VTankNodeTypes.DamagePercentGE              => TypedEditors.SingleDoubleEditor(vm, onChange, "Damage % ≥"),
        VTankNodeTypes.TotalRatingsGE               => TypedEditors.SingleIntEditor(vm, onChange, "Total ratings ≥"),
        VTankNodeTypes.BuffedMedianDamageGE         => TypedEditors.SingleDoubleEditor(vm, onChange, "Median damage ≥"),
        VTankNodeTypes.BuffedMissileDamageGE        => TypedEditors.SingleDoubleEditor(vm, onChange, "Missile damage ≥"),
        VTankNodeTypes.CalcdBuffedTinkedDamageGE    => TypedEditors.SingleDoubleEditor(vm, onChange, "Tinked damage ≥"),
        VTankNodeTypes.CharacterLevelGE             => TypedEditors.SingleIntEditor(vm, onChange, "Char level ≥"),
        VTankNodeTypes.CharacterLevelLE             => TypedEditors.SingleIntEditor(vm, onChange, "Char level ≤"),
        VTankNodeTypes.CharacterMainPackEmptySlotsGE=> TypedEditors.SingleIntEditor(vm, onChange, "Pack slots ≥"),
        VTankNodeTypes.CharacterSkillGE             => TypedEditors.SkillGEEditor(vm, onChange),
        VTankNodeTypes.CharacterBaseSkill           => TypedEditors.BaseSkillEditor(vm, onChange),
        VTankNodeTypes.SlotExactPalette             => TypedEditors.TwoIntEditor(vm, onChange, "Slot", "Palette ID"),
        VTankNodeTypes.AnySimilarColor              => TypedEditors.ColorEditor(vm, onChange, hasArmorOrSlot: false),
        VTankNodeTypes.SimilarColorArmorType        => TypedEditors.ColorEditor(vm, onChange, hasArmorOrSlot: true,  trailingLabel: "Armor group"),
        VTankNodeTypes.SlotSimilarColor             => TypedEditors.ColorEditor(vm, onChange, hasArmorOrSlot: true,  trailingLabel: "Slot"),
        VTankNodeTypes.CalcedBuffedTinkedTargetMeleeGE
            => TypedEditors.ThreeDoubleEditor(vm, onChange, "Target DoT", "Target melee def", "Target attack"),
        VTankNodeTypes.DisabledRule                 => TypedEditors.DisabledRuleEditor(vm, onChange),
        _ => null,
    };
}

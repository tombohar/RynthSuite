using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using RynthCore.Loot;
using System;

namespace RynthCore.LootEditor;

/// <summary>
/// Two-level condition editor: Category → Operator/Sub-type → Fields.
/// Category change and operator change replace the underlying condition type;
/// field edits mutate the existing condition in place.
/// </summary>
public class ConditionEditorView : StackPanel
{
    // ── Categories ────────────────────────────────────────────────────────────

    private enum Category
    {
        ObjectClass,
        LongProperty,
        DoubleProperty,
        StringProperty,
        ComputedStat,
        CharacterSkill,
    }

    private static readonly string[] CategoryLabels =
        ["Object Class", "Long Property", "Double Property", "String Property", "Computed Stat", "Character Skill"];

    private static Category GetCategory(LootCondition c) => c switch
    {
        ObjectClassCondition                                                                      => Category.ObjectClass,
        LongValKeyGECondition or LongValKeyLECondition or LongValKeyECondition
            or LongValKeyNECondition or LongValKeyFlagCondition                                   => Category.LongProperty,
        DoubleValKeyGECondition or DoubleValKeyLECondition                                        => Category.DoubleProperty,
        StringValueCondition                                                                      => Category.StringProperty,
        TotalRatingsGECondition or MinDamageGECondition or DamagePercentGECondition               => Category.ComputedStat,
        CharacterSkillGECondition                                                                 => Category.CharacterSkill,
        _                                                                                         => Category.LongProperty,
    };

    // ── Long operators ────────────────────────────────────────────────────────

    private static readonly string[] LongOpLabels = [">=", "<=", "==", "!=", "has flag"];

    private static int GetLongOpIndex(LootCondition c) => c switch
    {
        LongValKeyGECondition   => 0,
        LongValKeyLECondition   => 1,
        LongValKeyECondition    => 2,
        LongValKeyNECondition   => 3,
        LongValKeyFlagCondition => 4,
        _                       => 0,
    };

    private static LootCondition MakeLongCond(int opIdx, int key, int value) => opIdx switch
    {
        0 => new LongValKeyGECondition   { Key = key, Value     = value },
        1 => new LongValKeyLECondition   { Key = key, Value     = value },
        2 => new LongValKeyECondition    { Key = key, Value     = value },
        3 => new LongValKeyNECondition   { Key = key, Value     = value },
        4 => new LongValKeyFlagCondition { Key = key, FlagValue = value },
        _ => new LongValKeyGECondition   { Key = key, Value     = value },
    };

    private static (int key, int value) GetLongKeyValue(LootCondition c) => c switch
    {
        LongValKeyGECondition   x => (x.Key, x.Value),
        LongValKeyLECondition   x => (x.Key, x.Value),
        LongValKeyECondition    x => (x.Key, x.Value),
        LongValKeyNECondition   x => (x.Key, x.Value),
        LongValKeyFlagCondition x => (x.Key, x.FlagValue),
        _                         => (0, 0),
    };

    // ── Double operators ──────────────────────────────────────────────────────

    private static readonly string[] DoubleOpLabels = [">=", "<="];

    private static int GetDoubleOpIndex(LootCondition c) => c switch
    {
        DoubleValKeyGECondition => 0,
        DoubleValKeyLECondition => 1,
        _                       => 0,
    };

    private static LootCondition MakeDoubleCond(int opIdx, int key, double value) => opIdx switch
    {
        0 => new DoubleValKeyGECondition { Key = key, Value = value },
        1 => new DoubleValKeyLECondition { Key = key, Value = value },
        _ => new DoubleValKeyGECondition { Key = key, Value = value },
    };

    // ── Computed sub-types ────────────────────────────────────────────────────

    private static readonly string[] ComputedLabels = ["Total Ratings", "Min Damage", "Damage %"];

    private static int GetComputedIndex(LootCondition c) => c switch
    {
        TotalRatingsGECondition  => 0,
        MinDamageGECondition     => 1,
        DamagePercentGECondition => 2,
        _                        => 0,
    };

    // ── State ─────────────────────────────────────────────────────────────────

    private readonly LootCondition _cond;
    private readonly MainViewModel _vm;
    private bool _handlersRegistered;

    // ── Constructor ───────────────────────────────────────────────────────────

    public ConditionEditorView(LootCondition cond, MainViewModel vm)
    {
        _cond   = cond;
        _vm     = vm;
        Spacing = 6;
        try { Build(); }
        catch (Exception ex) { Program.Log($"ConditionEditorView.Build crashed: {ex}"); throw; }
    }

    // ── Build ─────────────────────────────────────────────────────────────────

    private void Build()
    {
        var category = GetCategory(_cond);

        // Row 1: Category
        var catCombo = Combo(CategoryLabels, (int)category);
        Children.Add(Row("Category", catCombo));

        // Rows 2+: Category-specific. Build methods return an Action that
        // registers SelectionChanged handlers calling ReplaceCondition (if any).
        Action? registerCategoryHandlers = null;
        switch (category)
        {
            case Category.ObjectClass:    BuildObjectClass(); break;
            case Category.LongProperty:   registerCategoryHandlers = BuildLong();     break;
            case Category.DoubleProperty: registerCategoryHandlers = BuildDouble();   break;
            case Category.StringProperty: BuildString(); break;
            case Category.ComputedStat:   registerCategoryHandlers = BuildComputed(); break;
            case Category.CharacterSkill: BuildSkill(); break;
        }

        // All SelectionChanged handlers that can trigger ReplaceCondition are registered
        // via Dispatcher.UIThread.Post inside AttachedToVisualTree.  This defers them to
        // the next dispatch cycle, after any attachment-triggered SelectionChanged events
        // on the new ComboBoxes have already fired (and found no handlers).
        this.AttachedToVisualTree += (_, _) =>
            Dispatcher.UIThread.Post(() =>
            {
                // Guard against double-registration if the view is detached/re-attached.
                if (_handlersRegistered || VisualRoot == null) return;
                _handlersRegistered = true;

                catCombo.SelectionChanged += (_, _) =>
                {
                    if (catCombo.SelectedIndex < 0) return;
                    var newCond = (Category)catCombo.SelectedIndex switch
                    {
                        Category.ObjectClass    => (LootCondition)new ObjectClassCondition(),
                        Category.LongProperty   => new LongValKeyGECondition(),
                        Category.DoubleProperty => new DoubleValKeyGECondition(),
                        Category.StringProperty => new StringValueCondition(),
                        Category.ComputedStat   => new TotalRatingsGECondition(),
                        Category.CharacterSkill => new CharacterSkillGECondition(),
                        _                       => new LongValKeyGECondition(),
                    };
                    _vm.ReplaceCondition(_cond, newCond);
                };

                registerCategoryHandlers?.Invoke();
            });
    }

    private void BuildObjectClass()
    {
        var x = (ObjectClassCondition)_cond;
        var combo = EnumCombo<AcObjectClass>(x.ObjectClass, v => { x.ObjectClass = v; _vm.NotifyConditionChanged(); });
        Children.Add(Row("Class", combo));
    }

    // Returns an Action that registers the operator / key SelectionChanged handlers.
    private Action BuildLong()
    {
        var (key, value) = GetLongKeyValue(_cond);
        int currentKey   = key;
        int currentValue = value;

        // Operator row
        var opCombo = Combo(LongOpLabels, GetLongOpIndex(_cond));
        Children.Add(Row("Operator", opCombo));

        // Property row — named dropdown, falls back to raw int if unknown
        var keyCombo = IntEnumCombo<AcIntProperty>(currentKey, v =>
        {
            currentKey = v;
            SetLongKey(_cond, v);
            _vm.NotifyConditionChanged();
        });
        Children.Add(Row("Property", keyCombo));

        // Value/Flag row — ContentControl host lets us swap the inner control without touching Children
        Control MakeValControl() => currentKey == (int)AcIntProperty.MaterialType
            ? IntEnumCombo<AcMaterialType>(currentValue, v => { currentValue = v; SetLongValue(_cond, v); _vm.NotifyConditionChanged(); })
            : (Control)IntBox(currentValue,              v => { currentValue = v; SetLongValue(_cond, v); _vm.NotifyConditionChanged(); });

        string ValLabel() => opCombo.SelectedIndex == 4 ? "Flag" : "Value";
        var valueHost = new ContentControl { HorizontalAlignment = HorizontalAlignment.Stretch };
        valueHost.Content = MakeValControl();
        Children.Add(Row(ValLabel(), valueHost));

        return () =>
        {
            keyCombo.SelectionChanged += (_, _) =>
            {
                if (keyCombo.SelectedItem is IntEnumItem ki)
                {
                    currentKey = ki.Value;
                    SetLongKey(_cond, ki.Value);
                    _vm.NotifyConditionChanged();
                    valueHost.Content = MakeValControl();
                    // Auto-select == operator for enum-like properties (MaterialType)
                    if (ki.Value == (int)AcIntProperty.MaterialType && opCombo.SelectedIndex != 2)
                        opCombo.SelectedIndex = 2; // triggers opCombo.SelectionChanged → ReplaceCondition
                }
            };

            opCombo.SelectionChanged += (_, _) =>
            {
                if (opCombo.SelectedIndex < 0) return;
                _vm.ReplaceCondition(_cond, MakeLongCond(opCombo.SelectedIndex, currentKey, currentValue));
            };
        };
    }

    // Returns an Action that registers the operator SelectionChanged handler.
    private Action BuildDouble()
    {
        var x = _cond switch
        {
            DoubleValKeyGECondition g => (key: g.Key, value: g.Value),
            DoubleValKeyLECondition l => (key: l.Key, value: l.Value),
            _                         => (key: 0,     value: 0.0),
        };
        int    currentKey   = x.key;
        double currentValue = x.value;

        var opCombo = Combo(DoubleOpLabels, GetDoubleOpIndex(_cond));
        Children.Add(Row("Operator", opCombo));

        var keyCombo = IntEnumCombo<AcFloatProperty>(currentKey, v =>
        {
            currentKey = v;
            SetDoubleKey(_cond, v);
            _vm.NotifyConditionChanged();
        });
        Children.Add(Row("Property", keyCombo));

        var valueBox = DoubleBox(currentValue, v =>
        {
            currentValue = v;
            SetDoubleValue(_cond, v);
            _vm.NotifyConditionChanged();
        });
        Children.Add(Row("Value", valueBox));

        return () =>
            opCombo.SelectionChanged += (_, _) =>
            {
                if (opCombo.SelectedIndex < 0) return;
                _vm.ReplaceCondition(_cond, MakeDoubleCond(opCombo.SelectedIndex, currentKey, currentValue));
            };
    }

    private void BuildString()
    {
        var x = (StringValueCondition)_cond;

        var keyBox = IntBox(x.Key, v => { x.Key = v; _vm.NotifyConditionChanged(); });
        Children.Add(Row("Key", keyBox));

        var patternBox = StyledTextBox(x.Pattern);
        patternBox.TextChanged += (_, _) => { x.Pattern = patternBox.Text ?? ""; _vm.NotifyConditionChanged(); };
        Children.Add(Row("Pattern", patternBox));
    }

    // Returns an Action that registers the sub-type SelectionChanged handler.
    private Action BuildComputed()
    {
        var subCombo = Combo(ComputedLabels, GetComputedIndex(_cond));

        double currentValue = _cond switch
        {
            TotalRatingsGECondition  t => t.Value,
            MinDamageGECondition     m => m.Value,
            DamagePercentGECondition d => d.Value,
            _                         => 0,
        };

        Children.Add(Row("Stat", subCombo));

        var valueBox = DoubleBox(currentValue, v =>
        {
            currentValue = v;
            SetComputedValue(_cond, v);
            _vm.NotifyConditionChanged();
        });
        Children.Add(Row("Value", valueBox));

        return () =>
            subCombo.SelectionChanged += (_, _) =>
            {
                if (subCombo.SelectedIndex < 0) return;
                LootCondition newCond = subCombo.SelectedIndex switch
                {
                    0 => new TotalRatingsGECondition  { Value = (int)currentValue },
                    1 => new MinDamageGECondition     { Value = currentValue },
                    2 => new DamagePercentGECondition { Value = currentValue },
                    _ => new TotalRatingsGECondition  { Value = (int)currentValue },
                };
                _vm.ReplaceCondition(_cond, newCond);
            };
    }

    private void BuildSkill()
    {
        var x = (CharacterSkillGECondition)_cond;
        Children.Add(Row("Skill",     EnumCombo<AcSkillType>(x.Skill, v => { x.Skill = v; _vm.NotifyConditionChanged(); })));
        Children.Add(Row("Min Level", IntBox(x.Value,                  v => { x.Value = v; _vm.NotifyConditionChanged(); })));
    }

    // ── Condition mutation helpers ────────────────────────────────────────────

    private static void SetLongKey(LootCondition cond, int key)
    {
        switch (cond)
        {
            case LongValKeyGECondition   x: x.Key = key; break;
            case LongValKeyLECondition   x: x.Key = key; break;
            case LongValKeyECondition    x: x.Key = key; break;
            case LongValKeyNECondition   x: x.Key = key; break;
            case LongValKeyFlagCondition x: x.Key = key; break;
        }
    }

    private static void SetLongValue(LootCondition cond, int value)
    {
        switch (cond)
        {
            case LongValKeyGECondition   x: x.Value     = value; break;
            case LongValKeyLECondition   x: x.Value     = value; break;
            case LongValKeyECondition    x: x.Value     = value; break;
            case LongValKeyNECondition   x: x.Value     = value; break;
            case LongValKeyFlagCondition x: x.FlagValue = value; break;
        }
    }

    private static void SetDoubleKey(LootCondition cond, int key)
    {
        switch (cond)
        {
            case DoubleValKeyGECondition x: x.Key = key; break;
            case DoubleValKeyLECondition x: x.Key = key; break;
        }
    }

    private static void SetDoubleValue(LootCondition cond, double value)
    {
        switch (cond)
        {
            case DoubleValKeyGECondition x: x.Value = value; break;
            case DoubleValKeyLECondition x: x.Value = value; break;
        }
    }

    private static void SetComputedValue(LootCondition cond, double value)
    {
        switch (cond)
        {
            case TotalRatingsGECondition  x: x.Value = (int)value; break;
            case MinDamageGECondition     x: x.Value = value;      break;
            case DamagePercentGECondition x: x.Value = value;      break;
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static readonly IBrush MutedBrush = new SolidColorBrush(Color.Parse("#7D8FA1"));

    private static Grid Row(string label, Control control)
    {
        var lbl = new TextBlock
        {
            Text              = label,
            Foreground        = MutedBrush,
            VerticalAlignment = VerticalAlignment.Center,
        };
        return Row(lbl, control);
    }

    private static Grid Row(TextBlock label, Control control)
    {
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(label,   0);
        Grid.SetColumn(control, 1);
        g.Children.Add(label);
        g.Children.Add(control);
        return g;
    }

    private static ComboBox Combo(string[] items, int selectedIndex)
    {
        var cb = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var item in items) cb.Items.Add(item);
        cb.SelectedIndex = selectedIndex;
        return cb;
    }

    private static ComboBox EnumCombo<T>(T current, Action<T> onChange) where T : struct, Enum
    {
        var cb = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var v in Enum.GetValues<T>()) cb.Items.Add(v);
        cb.SelectedItem = current;
        cb.SelectionChanged += (_, _) => { if (cb.SelectedItem is T v) onChange(v); };
        return cb;
    }

    /// <summary>
    /// ComboBox for an enum whose members have explicit int values (AcIntProperty,
    /// AcFloatProperty). Stores/retrieves as plain int so the model Key field is unaffected.
    /// Unknown values show as "(raw: N)".
    /// </summary>
    private static ComboBox IntEnumCombo<T>(int current, Action<int> onChange) where T : struct, Enum
    {
        var cb = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };

        // Populate with all known named values
        int selectedIdx = 0;
        int i = 0;
        foreach (var v in Enum.GetValues<T>())
        {
            int numVal = Convert.ToInt32(v);
            cb.Items.Add(new IntEnumItem(v.ToString(), numVal));
            if (numVal == current) selectedIdx = i;
            i++;
        }
        // If current isn't a known value, add a raw fallback entry
        if (current != 0 && !Enum.IsDefined(typeof(T), current))
        {
            cb.Items.Add(new IntEnumItem($"(raw: {current})", current));
            selectedIdx = cb.Items.Count - 1;
        }

        cb.SelectedIndex = selectedIdx;
        cb.SelectionChanged += (_, _) =>
        {
            if (cb.SelectedItem is IntEnumItem item) onChange(item.Value);
        };
        return cb;
    }

    private sealed record IntEnumItem(string Label, int Value)
    {
        public override string ToString() => Label;
    }

    private TextBox IntBox(int initial, Action<int> onChange)
    {
        var tb = StyledTextBox(initial.ToString());
        tb.TextChanged += (_, _) => { if (int.TryParse(tb.Text, out int v)) onChange(v); };
        return tb;
    }

    private TextBox DoubleBox(double initial, Action<double> onChange)
    {
        var tb = StyledTextBox(initial.ToString());
        tb.TextChanged += (_, _) => { if (double.TryParse(tb.Text, out double v)) onChange(v); };
        return tb;
    }

    private static TextBox StyledTextBox(string text) => new()
    {
        Text            = text,
        Background      = new SolidColorBrush(Color.Parse("#141C26")),
        Foreground      = Brushes.White,
        BorderBrush     = new SolidColorBrush(Color.Parse("#264059")),
        BorderThickness = new Thickness(1),
        Padding         = new Thickness(5, 3),
        CornerRadius    = new CornerRadius(3),
    };
}

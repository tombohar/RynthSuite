using Avalonia.Media;
using RynthCore.Loot.VTank;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text;

namespace RynthCore.LootEditor;

/// <summary>
/// Observable view-model wrapping a VTankLootRule. Edits flow into the
/// underlying data so save reproduces them; INPC drives the Avalonia bindings.
/// </summary>
public sealed class VTankRuleVm : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Notify([CallerMemberName] string? n = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));

    public VTankLootRule Data { get; }

    public VTankRuleVm(VTankLootRule data)
    {
        Data = data;
        foreach (var c in data.Conditions)
        {
            var vm = new VTankConditionVm(c) { OwnerRule = this };
            Conditions.Add(vm);
        }
        Conditions.CollectionChanged += OnConditionsChanged;
    }

    /// <summary>Called by a child condition VM when its data changed.</summary>
    internal void NotifySummaryChanged() => Notify(nameof(Summary));

    private void OnConditionsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // Keep OwnerRule wired so Summary refreshes when descendants edit data.
        if (e.NewItems != null)
            foreach (var obj in e.NewItems)
                if (obj is VTankConditionVm cvm) cvm.OwnerRule = this;
        Notify(nameof(ConditionCount));
        Notify(nameof(ConditionCountLabel));
        Notify(nameof(Summary));
        Notify(nameof(Enabled));
    }

    public string Name
    {
        get => Data.Name;
        set { if (Data.Name == value) return; Data.Name = value; Notify(); }
    }

    public string CustomExpression
    {
        get => Data.CustomExpression ?? string.Empty;
        set
        {
            string v = value ?? string.Empty;
            if ((Data.CustomExpression ?? string.Empty) == v) return;
            Data.CustomExpression = v;
            Notify();
        }
    }

    public int Priority
    {
        get => Data.Priority;
        set { if (Data.Priority == value) return; Data.Priority = value; Notify(); }
    }

    public VTankLootAction Action
    {
        get => Data.Action;
        set
        {
            if (Data.Action == value) return;
            Data.Action = value;
            // Maintain the keep-count line invariant: present iff KeepUpTo.
            if (value == VTankLootAction.KeepUpTo)
                Data.KeepCount ??= 1;
            else
                Data.KeepCount = null;
            Notify();
            Notify(nameof(KeepCount));
            Notify(nameof(ShowKeepCount));
            Notify(nameof(ActionLabel));
            Notify(nameof(ActionBadgeBrush));
            Notify(nameof(ActionBadgeForeground));
        }
    }

    public string ActionLabel => Data.Action switch
    {
        VTankLootAction.Keep     => "KEEP",
        VTankLootAction.KeepUpTo => $"KEEP {Data.KeepCount ?? 0}",
        VTankLootAction.Salvage  => "SALVAGE",
        VTankLootAction.Sell     => "SELL",
        VTankLootAction.Read     => "READ",
        _                        => "?",
    };

    public IBrush ActionBadgeBrush => Data.Action switch
    {
        VTankLootAction.Keep     => new SolidColorBrush(Color.Parse("#1f6f3a")), // green
        VTankLootAction.KeepUpTo => new SolidColorBrush(Color.Parse("#196e6e")), // teal
        VTankLootAction.Salvage  => new SolidColorBrush(Color.Parse("#a35c15")), // orange
        VTankLootAction.Sell     => new SolidColorBrush(Color.Parse("#7a1f1f")), // red
        VTankLootAction.Read     => new SolidColorBrush(Color.Parse("#1f497d")), // blue
        _                        => new SolidColorBrush(Color.Parse("#444"   )), // gray
    };

    public IBrush ActionBadgeForeground => new SolidColorBrush(Color.Parse("#FFFFFF"));

    public int ConditionCount => Conditions.Count;
    public string ConditionCountLabel => ConditionCount == 0
        ? "always"
        : (ConditionCount == 1 ? "1 cond" : $"{ConditionCount} conds");

    public int KeepCount
    {
        get => Data.KeepCount ?? 0;
        set
        {
            if ((Data.KeepCount ?? 0) == value) return;
            Data.KeepCount = value;
            Notify();
        }
    }

    public bool ShowKeepCount => Data.Action == VTankLootAction.KeepUpTo;

    public bool Enabled
    {
        get => Data.Enabled;
        set
        {
            if (Data.Enabled == value) return;
            Data.Enabled = value;
            Notify();
            // Enabled toggle adds/removes a DisabledRule condition node — keep
            // the VM-side condition list in sync so the UI grid updates.
            SyncConditionsFromData();
        }
    }

    public ObservableCollection<VTankConditionVm> Conditions { get; } = new();

    /// <summary>One-line plain-English description of all conditions.</summary>
    public string Summary
    {
        get
        {
            if (Conditions.Count == 0) return "Matches every item.";
            var sb = new StringBuilder();
            for (int i = 0; i < Conditions.Count; i++)
            {
                if (i > 0) sb.Append(" AND ");
                sb.Append(SummarizeCondition(Conditions[i]));
            }
            return sb.ToString();
        }
    }

    private static string SummarizeCondition(VTankConditionVm c)
    {
        var d = c.Data.DataLines;
        string lk(int i) => LookupName(TypedEditors.LongKeys,   Get(d, i));
        string dk(int i) => LookupName(TypedEditors.DoubleKeys, Get(d, i));
        string sk(int i) => LookupName(TypedEditors.StringKeys, Get(d, i));
        string skl(int i) => LookupName(TypedEditors.Skills,    Get(d, i));
        string oc(int i)
        {
            if (int.TryParse(Get(d, i), out int n))
                foreach (var oct in TypedEditors.ObjectClassesAccess())
                    if (oct.Id == n) return oct.Name;
            return Get(d, i);
        }

        return c.Data.NodeType switch
        {
            VTankNodeTypes.ObjectClass                  => $"Class is {oc(0)}",
            VTankNodeTypes.LongValKeyGE                 => $"{lk(1)} ≥ {Get(d, 0)}",
            VTankNodeTypes.LongValKeyLE                 => $"{lk(1)} ≤ {Get(d, 0)}",
            VTankNodeTypes.LongValKeyE                  => $"{lk(1)} = {Get(d, 0)}",
            VTankNodeTypes.LongValKeyNE                 => $"{lk(1)} ≠ {Get(d, 0)}",
            VTankNodeTypes.LongValKeyFlagExists         => $"{lk(1)} has flag {Get(d, 0)}",
            VTankNodeTypes.DoubleValKeyGE               => $"{dk(1)} ≥ {Get(d, 0)}",
            VTankNodeTypes.DoubleValKeyLE               => $"{dk(1)} ≤ {Get(d, 0)}",
            VTankNodeTypes.BuffedLongValKeyGE           => $"buffed {lk(1)} ≥ {Get(d, 0)}",
            VTankNodeTypes.BuffedDoubleValKeyGE         => $"buffed {dk(1)} ≥ {Get(d, 0)}",
            VTankNodeTypes.StringValueMatch             => $"{sk(1)} matches \"{Trunc(Get(d, 0), 30)}\"",
            VTankNodeTypes.SpellNameMatch               => $"spell matches \"{Trunc(Get(d, 0), 30)}\"",
            VTankNodeTypes.SpellCountGE                 => $"spell count ≥ {Get(d, 0)}",
            VTankNodeTypes.SpellMatch                   => $"≥{Get(d, 2)} spells matching \"{Trunc(Get(d, 0), 20)}\"",
            VTankNodeTypes.MinDamageGE                  => $"min dmg ≥ {Get(d, 0)}",
            VTankNodeTypes.DamagePercentGE              => $"dmg% ≥ {Get(d, 0)}",
            VTankNodeTypes.TotalRatingsGE               => $"total ratings ≥ {Get(d, 0)}",
            VTankNodeTypes.BuffedMedianDamageGE         => $"median dmg ≥ {Get(d, 0)}",
            VTankNodeTypes.BuffedMissileDamageGE        => $"missile dmg ≥ {Get(d, 0)}",
            VTankNodeTypes.CalcdBuffedTinkedDamageGE    => $"tinked dmg ≥ {Get(d, 0)}",
            VTankNodeTypes.CharacterLevelGE             => $"char level ≥ {Get(d, 0)}",
            VTankNodeTypes.CharacterLevelLE             => $"char level ≤ {Get(d, 0)}",
            VTankNodeTypes.CharacterMainPackEmptySlotsGE=> $"pack slots ≥ {Get(d, 0)}",
            VTankNodeTypes.CharacterSkillGE             => $"{skl(1)} skill ≥ {Get(d, 0)}",
            VTankNodeTypes.CharacterBaseSkill           => $"{skl(0)} base in [{Get(d, 1)}, {Get(d, 2)}]",
            VTankNodeTypes.SlotExactPalette             => $"palette slot {Get(d, 0)} = {Get(d, 1)}",
            VTankNodeTypes.AnySimilarColor              => "color (any similar)",
            VTankNodeTypes.SimilarColorArmorType        => "color (armor type)",
            VTankNodeTypes.SlotSimilarColor             => "color (slot)",
            VTankNodeTypes.CalcedBuffedTinkedTargetMeleeGE => "buffed tinked target melee",
            VTankNodeTypes.DisabledRule                 => "(rule disabled)",
            _ => $"node {c.Data.NodeType}",
        };

        static string Get(System.Collections.Generic.IList<string> lines, int idx)
            => idx < lines.Count ? lines[idx] : "?";
        static string Trunc(string s, int n) => s.Length > n ? s.Substring(0, n) + "…" : s;

        static string LookupName((int Id, string Name)[] table, string raw)
        {
            if (int.TryParse(raw, out int n))
                foreach (var t in table)
                    if (t.Id == n) return t.Name;
            return $"key {raw}";
        }
    }

    /// <summary>Re-syncs the VM condition list with the underlying data list.</summary>
    public void SyncConditionsFromData()
    {
        Conditions.Clear();
        foreach (var c in Data.Conditions)
            Conditions.Add(new VTankConditionVm(c));
    }

    /// <summary>Push current VM condition order/state back into Data before save.</summary>
    public void FlushConditionsToData()
    {
        Data.Conditions.Clear();
        foreach (var vm in Conditions)
            Data.Conditions.Add(vm.Data);
    }
}

/// <summary>Observable view-model for a single VTankLootCondition.</summary>
public sealed class VTankConditionVm : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Notify([CallerMemberName] string? n = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));

    public VTankLootCondition Data { get; }

    public VTankConditionVm(VTankLootCondition data) { Data = data; }

    public int NodeType
    {
        get => Data.NodeType;
        set
        {
            if (Data.NodeType == value) return;
            Data.NodeType = value;
            Notify();
            Notify(nameof(DisplayName));
        }
    }

    public string DisplayName => $"{VTankNodeTypes.DisplayName(Data.NodeType)}  {Summarize()}";

    /// <summary>One-liner summary of the condition's data for the list view.</summary>
    private string Summarize()
    {
        var d = Data.DataLines;
        if (d.Count == 0) return string.Empty;
        if (d.Count == 1) return $"= {d[0]}";
        // For two-line conditions value/key is the typical shape.
        if (d.Count == 2) return $"= {d[0]} (key {d[1]})";
        return $"({d.Count} lines)";
    }

    /// <summary>Force the list-view summary to re-render after a data-line edit.</summary>
    public void NotifyDataChanged()
    {
        Notify(nameof(DisplayName));
        OwnerRule?.NotifySummaryChanged();
    }

    /// <summary>The rule that owns this condition; set by VTankRuleVm at construction.</summary>
    internal VTankRuleVm? OwnerRule { get; set; }
}

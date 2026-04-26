using Avalonia.Controls;
using Avalonia.Platform.Storage;
using RynthCore.Loot;
using RynthCore.Loot.VTank;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;

namespace RynthCore.LootEditor;

public class MainViewModel : INotifyPropertyChanged
{
    private const string DefaultFolder = @"C:\Games\RynthSuite\RynthAi\LootProfiles";

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
    private void Notify([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    // ── State ────────────────────────────────────────────────────────────────

    private VTankLootProfile _profile = new();
    private string? _filePath;
    private bool _dirty;
    private readonly Window _window;

    // ── Rule list ────────────────────────────────────────────────────────────

    public ObservableCollection<VTankRuleVm> Rules { get; } = new();
    public ObservableCollection<VTankRuleVm> FilteredRules { get; } = new();

    private string _searchText = "";
    public string SearchText
    {
        get => _searchText;
        set { Set(ref _searchText, value); ApplyFilter(); RefreshMoveCommands(); }
    }

    /// <summary>Action filter — null means "all actions".</summary>
    private VTankLootAction? _actionFilter;
    public VTankLootAction? ActionFilter
    {
        get => _actionFilter;
        set
        {
            Set(ref _actionFilter, value);
            ApplyFilter();
            // Re-evaluate every IsActionFilterX property so the chip buttons update.
            Notify(nameof(IsActionFilterAll));
            Notify(nameof(IsActionFilterKeep));
            Notify(nameof(IsActionFilterKeepUpTo));
            Notify(nameof(IsActionFilterSalvage));
            Notify(nameof(IsActionFilterSell));
            Notify(nameof(IsActionFilterRead));
            Notify(nameof(RuleCountLabel));
        }
    }
    public bool IsActionFilterAll      => _actionFilter == null;
    public bool IsActionFilterKeep     => _actionFilter == VTankLootAction.Keep;
    public bool IsActionFilterKeepUpTo => _actionFilter == VTankLootAction.KeepUpTo;
    public bool IsActionFilterSalvage  => _actionFilter == VTankLootAction.Salvage;
    public bool IsActionFilterSell     => _actionFilter == VTankLootAction.Sell;
    public bool IsActionFilterRead     => _actionFilter == VTankLootAction.Read;

    public string RuleCountLabel
    {
        get
        {
            int total = Rules.Count;
            int shown = FilteredRules.Count;
            return shown == total ? $"{total} rules" : $"{shown} of {total} rules";
        }
    }

    private void ApplyFilter()
    {
        FilteredRules.Clear();
        string q = _searchText.Trim();
        foreach (var r in Rules)
        {
            if (_actionFilter != null && r.Action != _actionFilter) continue;
            if (q.Length == 0 || r.Name.Contains(q, StringComparison.OrdinalIgnoreCase))
                FilteredRules.Add(r);
        }
        if (_selectedRule != null && !FilteredRules.Contains(_selectedRule))
            SelectedRule = null;
        Notify(nameof(RuleCountLabel));
    }

    private VTankRuleVm? _selectedRule;
    public VTankRuleVm? SelectedRule
    {
        get => _selectedRule;
        set
        {
            Set(ref _selectedRule, value);
            Notify(nameof(RuleEditorEnabled));
            Notify(nameof(Conditions));
            Notify(nameof(KeepCountVisible));
            SelectedCondition = null;
            RefreshMoveCommands();
        }
    }

    public bool RuleEditorEnabled => _selectedRule != null;
    public bool KeepCountVisible    => _selectedRule?.ShowKeepCount == true;

    // ── Conditions ───────────────────────────────────────────────────────────

    public ObservableCollection<VTankConditionVm>? Conditions => _selectedRule?.Conditions;

    private VTankConditionVm? _selectedCondition;
    public VTankConditionVm? SelectedCondition
    {
        get => _selectedCondition;
        set
        {
            Set(ref _selectedCondition, value);
            RebuildConditionEditor();
            CondDelete.RaiseCanExecuteChanged();
            CondMoveUp.RaiseCanExecuteChanged();
            CondMoveDown.RaiseCanExecuteChanged();
        }
    }

    private object? _conditionEditor;
    public object? ConditionEditor
    {
        get => _conditionEditor;
        private set => Set(ref _conditionEditor, value);
    }

    private void RebuildConditionEditor()
    {
        ConditionEditor = _selectedCondition == null
            ? null
            : new VTankConditionEditorView(_selectedCondition, MarkDirty);
    }

    private void MarkDirty()
    {
        _dirty = true;
        UpdateTitle();
    }

    // ── Status / title ───────────────────────────────────────────────────────

    private string _statusMessage = "Ready";
    public string StatusMessage
    {
        get => _statusMessage;
        private set => Set(ref _statusMessage, value);
    }
    private void Status(string msg) => StatusMessage = msg;

    private string _windowTitle = "RynthCore Loot Editor";
    public string WindowTitle
    {
        get => _windowTitle;
        private set => Set(ref _windowTitle, value);
    }
    private void UpdateTitle()
    {
        string baseName = string.IsNullOrEmpty(_filePath) ? "untitled.utl" : Path.GetFileName(_filePath);
        WindowTitle = $"RynthCore Loot Editor — {baseName}{(_dirty ? "*" : "")}";
    }

    // ── Picker option lists ──────────────────────────────────────────────────

    public IReadOnlyList<VTankLootAction> Actions { get; } = new[]
    {
        VTankLootAction.Keep,
        VTankLootAction.KeepUpTo,
        VTankLootAction.Salvage,
        VTankLootAction.Sell,
        VTankLootAction.Read,
    };

    public IReadOnlyList<NodeTypeOption> NodeTypeOptions { get; } = BuildNodeTypeOptions();
    private static List<NodeTypeOption> BuildNodeTypeOptions()
    {
        var list = new List<NodeTypeOption>();
        foreach (int t in VTankNodeTypes.All)
            list.Add(new NodeTypeOption(t, $"{VTankNodeTypes.DisplayName(t)}  (id {t})"));
        return list;
    }

    // ── Commands ─────────────────────────────────────────────────────────────

    public RelayCommand FileNew     { get; }
    public RelayCommand FileOpen    { get; }
    public RelayCommand FileSave    { get; }
    public RelayCommand FileSaveAs  { get; }
    public RelayCommand FileExit    { get; }
    public RelayCommand RuleAdd       { get; }
    public RelayCommand RuleDuplicate { get; }
    public RelayCommand RuleDelete    { get; }
    public RelayCommand RuleMoveUp    { get; }
    public RelayCommand RuleMoveDown  { get; }
    public RelayCommand SetActionFilter { get; }
    public RelayCommand CondAdd     { get; }
    public RelayCommand CondDelete  { get; }
    public RelayCommand CondMoveUp  { get; }
    public RelayCommand CondMoveDown{ get; }
    public RelayCommand SalvageRowAdd     { get; }
    public RelayCommand SalvageRowDelete  { get; }
    public RelayCommand SalvageImportUtl  { get; }

    public MainViewModel(Window window)
    {
        _window = window;

        FileNew      = new RelayCommand(_ => DoNew());
        FileOpen     = new RelayCommand(_ => _ = DoOpenAsync());
        FileSave     = new RelayCommand(_ => _ = DoSaveAsync(false));
        FileSaveAs   = new RelayCommand(_ => _ = DoSaveAsync(true));
        FileExit     = new RelayCommand(_ => _window.Close());
        RuleAdd       = new RelayCommand(_ => DoRuleAdd());
        RuleDuplicate = new RelayCommand(_ => DoRuleDuplicate(), _ => _selectedRule != null);
        RuleDelete    = new RelayCommand(_ => DoRuleDelete(), _ => _selectedRule != null);
        RuleMoveUp    = new RelayCommand(_ => DoRuleMove(-1), _ => CanMoveRule(-1));
        RuleMoveDown  = new RelayCommand(_ => DoRuleMove(1),  _ => CanMoveRule(1));
        SetActionFilter = new RelayCommand(arg =>
        {
            // Param is either a VTankLootAction (set the filter) or null (= "All").
            ActionFilter = arg switch
            {
                VTankLootAction a => a,
                string s when System.Enum.TryParse<VTankLootAction>(s, out var parsed) => parsed,
                _ => null,
            };
        });
        CondAdd      = new RelayCommand(_ => DoCondAdd());
        CondDelete   = new RelayCommand(_ => DoCondDelete(), _ => _selectedCondition != null);
        CondMoveUp   = new RelayCommand(_ => DoCondMove(-1), _ => CanMoveCond(-1));
        CondMoveDown = new RelayCommand(_ => DoCondMove(1),  _ => CanMoveCond(1));
        SalvageRowAdd    = new RelayCommand(_ => DoSalvageRowAdd());
        SalvageRowDelete = new RelayCommand(_ => DoSalvageRowDelete(), _ => _selectedSalvageRow != null);
        SalvageImportUtl = new RelayCommand(_ => _ = DoSalvageImportUtlAsync());

        LoadProfile(new VTankLootProfile());
    }

    // ── Load / save ──────────────────────────────────────────────────────────

    private void LoadProfile(VTankLootProfile p)
    {
        _profile = p;
        Rules.Clear();
        foreach (var r in p.Rules)
            Rules.Add(new VTankRuleVm(r));
        ApplyFilter();
        SelectedRule = null;
        LoadSalvageFromProfile(p);
        _dirty = false;
        UpdateTitle();
    }

    private void DoNew()
    {
        if (!ConfirmDiscard()) return;
        _filePath = null;
        SearchText = "";
        LoadProfile(new VTankLootProfile());
    }

    private async Task DoOpenAsync()
    {
        if (!ConfirmDiscard()) return;
        var opts = new FilePickerOpenOptions
        {
            Title          = "Open Loot Profile",
            AllowMultiple  = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("VTank / LootSnob") { Patterns = new[] { "*.utl" } },
                new FilePickerFileType("Legacy JSON")      { Patterns = new[] { "*.json" } },
                new FilePickerFileType("All Files")        { Patterns = new[] { "*.*" } },
            },
            SuggestedStartLocation = await GetFolderAsync(),
        };
        var files = await _window.StorageProvider.OpenFilePickerAsync(opts);
        if (files.Count == 0) return;

        var path = files[0].Path.LocalPath;
        try
        {
            VTankLootProfile loaded = path.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                ? ConvertFromJson(path)
                : VTankLootParser.Load(path);
            _filePath = path;
            SearchText = "";
            LoadProfile(loaded);
            Status($"Opened {Path.GetFileName(path)} ({loaded.Rules.Count} rules)");
        }
        catch (Exception ex)
        {
            await ShowError($"Failed to open profile:\n{ex.Message}");
        }
    }

    private static VTankLootProfile ConvertFromJson(string path)
    {
        // Minimal JSON → VTank converter for legacy LootEditor profiles.
        // Only the rule list is migrated; SalvageCombine carries over directly.
        var json = LootProfile.TryLoad(path) ?? new LootProfile();
        var vp = new VTankLootProfile { FileVersion = 1, SalvageCombine = json.SalvageCombine };
        foreach (var rule in json.Rules)
        {
            var vr = new VTankLootRule
            {
                Name = rule.Name,
                CustomExpression = string.Empty,
                Action = rule.Action switch
                {
                    LootAction.Keep     => VTankLootAction.Keep,
                    LootAction.KeepUpTo => VTankLootAction.KeepUpTo,
                    LootAction.Salvage  => VTankLootAction.Salvage,
                    LootAction.Sell     => VTankLootAction.Sell,
                    LootAction.Read     => VTankLootAction.Read,
                    _                   => VTankLootAction.Keep,
                },
                KeepCount = rule.Action == LootAction.KeepUpTo ? rule.KeepCount : (int?)null,
            };
            // Best-effort condition mapping — typed editor will surface anything
            // we couldn't map as raw lines.
            foreach (var c in rule.Conditions)
                vr.Conditions.Add(MapJsonCondition(c));
            if (!rule.Enabled) vr.Enabled = false;
            vp.Rules.Add(vr);
        }
        return vp;
    }

    private static VTankLootCondition MapJsonCondition(LootCondition c) => c switch
    {
        ObjectClassCondition o      => new(VTankNodeTypes.ObjectClass,        "0", new[] { o.ObjectClass.ToString() }),
        LongValKeyGECondition l     => new(VTankNodeTypes.LongValKeyGE,       "0", new[] { l.Value.ToString(), l.Key.ToString() }),
        LongValKeyLECondition l     => new(VTankNodeTypes.LongValKeyLE,       "0", new[] { l.Value.ToString(), l.Key.ToString() }),
        LongValKeyECondition l      => new(VTankNodeTypes.LongValKeyE,        "0", new[] { l.Value.ToString(), l.Key.ToString() }),
        LongValKeyNECondition l     => new(VTankNodeTypes.LongValKeyNE,       "0", new[] { l.Value.ToString(), l.Key.ToString() }),
        LongValKeyFlagCondition l   => new(VTankNodeTypes.LongValKeyFlagExists,"0", new[] { l.FlagValue.ToString(), l.Key.ToString() }),
        DoubleValKeyGECondition d   => new(VTankNodeTypes.DoubleValKeyGE,     "0", new[] { d.Value.ToString(System.Globalization.CultureInfo.InvariantCulture), d.Key.ToString() }),
        DoubleValKeyLECondition d   => new(VTankNodeTypes.DoubleValKeyLE,     "0", new[] { d.Value.ToString(System.Globalization.CultureInfo.InvariantCulture), d.Key.ToString() }),
        StringValueCondition s      => new(VTankNodeTypes.StringValueMatch,   "0", new[] { s.Pattern, s.Key.ToString() }),
        TotalRatingsGECondition t   => new(VTankNodeTypes.TotalRatingsGE,     "0", new[] { t.Value.ToString() }),
        MinDamageGECondition m      => new(VTankNodeTypes.MinDamageGE,        "0", new[] { m.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) }),
        DamagePercentGECondition d  => new(VTankNodeTypes.DamagePercentGE,    "0", new[] { d.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) }),
        CharacterSkillGECondition s => new(VTankNodeTypes.CharacterSkillGE,   "0", new[] { s.Value.ToString(), ((int)s.Skill).ToString() }),
        _ => new(VTankNodeTypes.ObjectClass, "0", new[] { "0" }), // unknown -> harmless placeholder
    };

    private async Task DoSaveAsync(bool saveAs)
    {
        if (!saveAs && _filePath != null) { Save(_filePath); return; }
        var opts = new FilePickerSaveOptions
        {
            Title             = "Save Loot Profile",
            SuggestedFileName = _filePath != null ? Path.GetFileNameWithoutExtension(_filePath) : "profile",
            DefaultExtension  = "utl",
            FileTypeChoices   = new[]
            {
                new FilePickerFileType("VTank / LootSnob") { Patterns = new[] { "*.utl" } },
                new FilePickerFileType("Legacy JSON")      { Patterns = new[] { "*.json" } },
            },
            SuggestedStartLocation = await GetFolderAsync(),
        };
        var file = await _window.StorageProvider.SaveFilePickerAsync(opts);
        if (file == null) return;
        _filePath = file.Path.LocalPath;
        Save(_filePath);
    }

    private void Save(string path)
    {
        // Push edits back into the underlying VTank data.
        FlushSalvageToProfile();
        _profile.Rules.Clear();
        foreach (var rvm in Rules)
        {
            rvm.FlushConditionsToData();
            _profile.Rules.Add(rvm.Data);
        }

        try
        {
            if (path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            {
                // Lossy export: drop conditions we can't represent and write JSON.
                var jp = ConvertToJson(_profile);
                jp.Save(path);
            }
            else
            {
                VTankLootWriter.Save(_profile, path);
            }
            _dirty = false;
            UpdateTitle();
            Status($"Saved {Path.GetFileName(path)}");
        }
        catch (Exception ex)
        {
            _ = ShowError($"Save failed:\n{ex.Message}");
        }
    }

    private static LootProfile ConvertToJson(VTankLootProfile vp)
    {
        // Minimal VTank → JSON exporter for backward compat. Conditions we
        // don't have a JSON class for are skipped — saving as JSON is lossy
        // by design.
        var jp = new LootProfile { Name = "", SalvageCombine = vp.SalvageCombine };
        foreach (var vr in vp.Rules)
        {
            var jr = new LootRule
            {
                Name = vr.Name,
                Enabled = vr.Enabled,
                Action = vr.Action switch
                {
                    VTankLootAction.Keep     => LootAction.Keep,
                    VTankLootAction.KeepUpTo => LootAction.KeepUpTo,
                    VTankLootAction.Salvage  => LootAction.Salvage,
                    VTankLootAction.Sell     => LootAction.Sell,
                    VTankLootAction.Read     => LootAction.Read,
                    _ => LootAction.Keep,
                },
                KeepCount = vr.KeepCount ?? 0,
            };
            jp.Rules.Add(jr);
        }
        return jp;
    }

    // ── Rule editing ─────────────────────────────────────────────────────────

    private void DoRuleAdd()
    {
        var data = new VTankLootRule { Name = "New Rule", CustomExpression = string.Empty, Action = VTankLootAction.Keep };
        var vm = new VTankRuleVm(data);
        Rules.Add(vm);
        ApplyFilter();
        SelectedRule = vm;
        MarkDirty();
    }

    private void DoRuleDuplicate()
    {
        if (_selectedRule == null) return;
        var src = _selectedRule.Data;
        var copy = new VTankLootRule
        {
            Name             = src.Name + " (copy)",
            CustomExpression = src.CustomExpression,
            Priority         = src.Priority,
            Action           = src.Action,
            KeepCount        = src.KeepCount,
        };
        foreach (var c in src.Conditions)
            copy.Conditions.Add(new VTankLootCondition(c.NodeType, c.LengthCode, c.DataLines));
        var vm = new VTankRuleVm(copy);
        int srcIdx = Rules.IndexOf(_selectedRule);
        Rules.Insert(srcIdx + 1, vm);
        ApplyFilter();
        SelectedRule = vm;
        MarkDirty();
    }

    private void DoRuleDelete()
    {
        if (_selectedRule == null) return;
        int idx = Rules.IndexOf(_selectedRule);
        Rules.Remove(_selectedRule);
        ApplyFilter();
        SelectedRule = idx < Rules.Count ? Rules[idx] : (Rules.Count > 0 ? Rules[^1] : null);
        MarkDirty();
    }

    private bool CanMoveRule(int dir)
    {
        if (_selectedRule == null) return false;
        int idx = Rules.IndexOf(_selectedRule);
        int newIdx = idx + dir;
        return newIdx >= 0 && newIdx < Rules.Count;
    }

    private void DoRuleMove(int dir)
    {
        if (!CanMoveRule(dir)) return;
        int idx = Rules.IndexOf(_selectedRule!);
        Rules.Move(idx, idx + dir);
        ApplyFilter();
        MarkDirty();
        RefreshMoveCommands();
    }

    private void RefreshMoveCommands()
    {
        RuleDelete.RaiseCanExecuteChanged();
        RuleMoveUp.RaiseCanExecuteChanged();
        RuleMoveDown.RaiseCanExecuteChanged();
    }

    // ── Condition editing ────────────────────────────────────────────────────

    private void DoCondAdd()
    {
        if (_selectedRule == null) return;
        var data = new VTankLootCondition(VTankNodeTypes.ObjectClass, "0", new[] { "0" });
        var vm = new VTankConditionVm(data);
        _selectedRule.Conditions.Add(vm);
        SelectedCondition = vm;
        MarkDirty();
    }

    private void DoCondDelete()
    {
        if (_selectedRule == null || _selectedCondition == null) return;
        int idx = _selectedRule.Conditions.IndexOf(_selectedCondition);
        _selectedRule.Conditions.Remove(_selectedCondition);
        SelectedCondition = idx < _selectedRule.Conditions.Count
            ? _selectedRule.Conditions[idx]
            : (_selectedRule.Conditions.Count > 0 ? _selectedRule.Conditions[^1] : null);
        MarkDirty();
    }

    private bool CanMoveCond(int dir)
    {
        if (_selectedRule == null || _selectedCondition == null) return false;
        int idx = _selectedRule.Conditions.IndexOf(_selectedCondition);
        int newIdx = idx + dir;
        return newIdx >= 0 && newIdx < _selectedRule.Conditions.Count;
    }

    private void DoCondMove(int dir)
    {
        if (!CanMoveCond(dir)) return;
        int idx = _selectedRule!.Conditions.IndexOf(_selectedCondition!);
        _selectedRule.Conditions.Move(idx, idx + dir);
        MarkDirty();
        CondMoveUp.RaiseCanExecuteChanged();
        CondMoveDown.RaiseCanExecuteChanged();
    }

    // ── Salvage Combine tab ──────────────────────────────────────────────────

    public ObservableCollection<SalvagePerMaterialRow> PerMaterialRows { get; } = new();

    public IReadOnlyList<MaterialOption> MaterialOptions { get; } = BuildMaterialOptions();
    private static List<MaterialOption> BuildMaterialOptions()
    {
        var list = new List<MaterialOption>();
        foreach (var kv in MaterialTypes.ByName)
            list.Add(new MaterialOption(kv.Key, $"{kv.Key} — {kv.Value}"));
        list.Sort((a, b) => string.Compare(a.Display, b.Display, StringComparison.OrdinalIgnoreCase));
        return list;
    }

    private bool _salvageEnabled = true;
    public bool SalvageEnabled
    {
        get => _salvageEnabled;
        set { Set(ref _salvageEnabled, value); MarkDirty(); }
    }

    private string _salvageDefaultBands = "1-6, 7-8, 9, 10";
    public string SalvageDefaultBands
    {
        get => _salvageDefaultBands;
        set { Set(ref _salvageDefaultBands, value); MarkDirty(); }
    }

    private SalvagePerMaterialRow? _selectedSalvageRow;
    public SalvagePerMaterialRow? SelectedSalvageRow
    {
        get => _selectedSalvageRow;
        set { Set(ref _selectedSalvageRow, value); SalvageRowDelete.RaiseCanExecuteChanged(); }
    }

    private void LoadSalvageFromProfile(VTankLootProfile p)
    {
        var sc = p.SalvageCombine;
        _salvageEnabled = sc?.Enabled ?? true;
        _salvageDefaultBands = sc?.DefaultBands ?? "1-6, 7-8, 9, 10";
        Notify(nameof(SalvageEnabled));
        Notify(nameof(SalvageDefaultBands));
        PerMaterialRows.Clear();
        if (sc != null)
            foreach (var kv in sc.PerMaterial)
                PerMaterialRows.Add(new SalvagePerMaterialRow(kv.Key, MaterialTypes.Name(kv.Key), kv.Value));
    }

    private void FlushSalvageToProfile()
    {
        // Only emit a SalvageCombine block if the user has configured anything;
        // empty defaults shouldn't litter freshly-created profiles.
        bool hasContent = PerMaterialRows.Count > 0 || !string.IsNullOrWhiteSpace(SalvageDefaultBands);
        if (!hasContent && (_profile.SalvageCombine == null || !_profile.SalvageCombine.Enabled))
            return;

        var sc = _profile.SalvageCombine ?? new SalvageCombineSettings();
        sc.Enabled      = SalvageEnabled;
        sc.DefaultBands = SalvageDefaultBands ?? string.Empty;
        sc.PerMaterial.Clear();
        foreach (var row in PerMaterialRows)
            if (row.MaterialId > 0)
                sc.PerMaterial[row.MaterialId] = row.BandsText ?? string.Empty;
        _profile.SalvageCombine = sc;
    }

    private void DoSalvageRowAdd()
    {
        int firstFree = 0;
        foreach (var opt in MaterialOptions)
        {
            bool used = false;
            foreach (var row in PerMaterialRows) if (row.MaterialId == opt.Id) { used = true; break; }
            if (!used) { firstFree = opt.Id; break; }
        }
        if (firstFree == 0 && MaterialOptions.Count > 0) firstFree = MaterialOptions[0].Id;
        var newRow = new SalvagePerMaterialRow(firstFree, MaterialTypes.Name(firstFree), "1-10");
        PerMaterialRows.Add(newRow);
        SelectedSalvageRow = newRow;
        MarkDirty();
    }

    private void DoSalvageRowDelete()
    {
        if (_selectedSalvageRow == null) return;
        int idx = PerMaterialRows.IndexOf(_selectedSalvageRow);
        PerMaterialRows.Remove(_selectedSalvageRow);
        MarkDirty();
        if (PerMaterialRows.Count > 0)
            SelectedSalvageRow = PerMaterialRows[Math.Min(idx, PerMaterialRows.Count - 1)];
        else
            SelectedSalvageRow = null;
    }

    private async Task DoSalvageImportUtlAsync()
    {
        var opts = new FilePickerOpenOptions
        {
            Title          = "Import SalvageCombine from VTank/LootSnob .utl",
            AllowMultiple  = false,
            FileTypeFilter = new[] { new FilePickerFileType("VTank Loot Profile") { Patterns = new[] { "*.utl" } } },
            SuggestedStartLocation = await GetFolderAsync(),
        };
        var files = await _window.StorageProvider.OpenFilePickerAsync(opts);
        if (files.Count == 0) return;

        var srcProfile = VTankLootParser.Load(files[0].Path.LocalPath);
        if (srcProfile.SalvageCombine == null)
        {
            await ShowError("That .utl file has no SalvageCombine block.");
            return;
        }

        var sc = srcProfile.SalvageCombine;
        SalvageEnabled = sc.Enabled;
        SalvageDefaultBands = sc.DefaultBands;
        PerMaterialRows.Clear();
        foreach (var kv in sc.PerMaterial)
            PerMaterialRows.Add(new SalvagePerMaterialRow(kv.Key, MaterialTypes.Name(kv.Key), kv.Value));
        MarkDirty();
        Status($"Imported SalvageCombine from {Path.GetFileName(files[0].Path.LocalPath)} ({sc.PerMaterial.Count} per-material rule(s)).");
    }

    // ── Plumbing helpers ─────────────────────────────────────────────────────

    private bool ConfirmDiscard()
    {
        if (!_dirty) return true;
        // Keep simple — auto-discard. (A confirm dialog can come later.)
        return true;
    }

    private async Task<IStorageFolder?> GetFolderAsync()
    {
        if (Directory.Exists(DefaultFolder))
            return await _window.StorageProvider.TryGetFolderFromPathAsync(DefaultFolder);
        return null;
    }

    private async Task ShowError(string msg)
    {
        await Task.Yield();
        Status(msg.Replace('\n', ' '));
    }
}

public sealed record NodeTypeOption(int Id, string Display);

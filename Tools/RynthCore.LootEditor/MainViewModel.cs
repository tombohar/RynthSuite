using Avalonia.Controls;
using Avalonia.Platform.Storage;
using RynthCore.Loot;
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

    // ── INotifyPropertyChanged ────────────────────────────────────────────────

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
    private void Notify([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    // ── State ─────────────────────────────────────────────────────────────────

    private LootProfile _profile = new();
    private string?     _filePath;
    private bool        _dirty;

    // ── Bound properties ──────────────────────────────────────────────────────

    public ObservableCollection<LootRule> Rules { get; } = new();

    public ObservableCollection<LootRule> FilteredRules { get; } = new();

    private string _searchText = "";
    public string SearchText
    {
        get => _searchText;
        set
        {
            Set(ref _searchText, value);
            ApplyFilter();
            RuleMoveUp.RaiseCanExecuteChanged();
            RuleMoveDown.RaiseCanExecuteChanged();
        }
    }

    private void ApplyFilter()
    {
        FilteredRules.Clear();
        string q = _searchText.Trim();
        foreach (var r in Rules)
        {
            if (q.Length == 0 || r.Name.Contains(q, StringComparison.OrdinalIgnoreCase))
                FilteredRules.Add(r);
        }
        if (_selectedRule != null && !FilteredRules.Contains(_selectedRule))
            SelectedRule = null;
    }

    private LootRule? _selectedRule;
    public LootRule? SelectedRule
    {
        get => _selectedRule;
        set
        {
            Set(ref _selectedRule, value);
            Notify(nameof(RuleEditorEnabled));
            Notify(nameof(SelectedRuleName));
            Notify(nameof(ShowKeepCount));
            Notify(nameof(KeepCountText));
            RuleDelete.RaiseCanExecuteChanged();
            RuleMoveUp.RaiseCanExecuteChanged();
            RuleMoveDown.RaiseCanExecuteChanged();
            RebuildConditions();
        }
    }

    public bool RuleEditorEnabled => _selectedRule != null;

    public string SelectedRuleName
    {
        get => _selectedRule?.Name ?? "";
        set
        {
            if (_selectedRule == null) return;
            _selectedRule.Name = value; // LootRule.Name fires its own INPC → ListBox updates in-place
            MarkDirty();
        }
    }

    public bool ShowKeepCount => _selectedRule?.Action == LootAction.KeepUpTo;

    public string KeepCountText
    {
        get => _selectedRule?.KeepCount.ToString() ?? "1";
        set
        {
            if (_selectedRule == null) return;
            if (int.TryParse(value, out int v)) { _selectedRule.KeepCount = v; MarkDirty(); }
        }
    }

    public IReadOnlyList<LootAction> LootActions { get; } =
        (LootAction[])Enum.GetValues(typeof(LootAction));

    public ObservableCollection<LootCondition> Conditions { get; } = new();

    private LootCondition? _selectedCondition;
    public LootCondition? SelectedCondition
    {
        get => _selectedCondition;
        set
        {
            Set(ref _selectedCondition, value);
            CondDelete.RaiseCanExecuteChanged();
            CondMoveUp.RaiseCanExecuteChanged();
            CondMoveDown.RaiseCanExecuteChanged();
            RebuildCondEditor();
        }
    }

    private object? _conditionEditor;
    public object? ConditionEditor
    {
        get => _conditionEditor;
        private set => Set(ref _conditionEditor, value);
    }

    private string _statusMessage = "Ready";
    public string StatusMessage
    {
        get => _statusMessage;
        private set => Set(ref _statusMessage, value);
    }

    // ── Commands (called from code-behind) ───────────────────────────────────

    public RelayCommand FileNew     { get; }
    public RelayCommand FileOpen    { get; }
    public RelayCommand FileSave    { get; }
    public RelayCommand FileSaveAs  { get; }
    public RelayCommand FileExit    { get; }
    public RelayCommand RuleAdd     { get; }
    public RelayCommand RuleDelete  { get; }
    public RelayCommand RuleMoveUp  { get; }
    public RelayCommand RuleMoveDown{ get; }
    public RelayCommand CondAdd     { get; }
    public RelayCommand CondDelete  { get; }
    public RelayCommand CondMoveUp  { get; }
    public RelayCommand CondMoveDown{ get; }

    // ── Window reference (for dialogs) ────────────────────────────────────────

    private readonly Window _window;

    public MainViewModel(Window window)
    {
        _window = window;

        FileNew      = new RelayCommand(_ => DoNew());
        FileOpen     = new RelayCommand(_ => _ = DoOpenAsync());
        FileSave     = new RelayCommand(_ => _ = DoSaveAsync(false));
        FileSaveAs   = new RelayCommand(_ => _ = DoSaveAsync(true));
        FileExit     = new RelayCommand(_ => _window.Close());
        RuleAdd      = new RelayCommand(_ => DoRuleAdd());
        RuleDelete   = new RelayCommand(_ => DoRuleDelete(), _ => _selectedRule != null);
        RuleMoveUp   = new RelayCommand(_ => DoRuleMove(-1), _ => CanMoveRule(-1));
        RuleMoveDown = new RelayCommand(_ => DoRuleMove(1),  _ => CanMoveRule(1));
        CondAdd      = new RelayCommand(_ => DoCondAdd());   // panel IsEnabled handles the rule-selected gate
        CondDelete   = new RelayCommand(_ => DoCondDelete(), _ => _selectedCondition != null);
        CondMoveUp   = new RelayCommand(_ => DoCondMove(-1), _ => CanMoveCond(-1));
        CondMoveDown = new RelayCommand(_ => DoCondMove(1),  _ => CanMoveCond(1));

        LoadProfile(new LootProfile { Name = "" });
    }

    // ── Profile load/save ────────────────────────────────────────────────────

    private void LoadProfile(LootProfile p)
    {
        _profile = p;
        Rules.Clear();
        foreach (var r in p.Rules) Rules.Add(r);
        ApplyFilter();
        SelectedRule = null;
        _dirty = false;
        UpdateTitle();
    }

    private void DoNew()
    {
        if (!ConfirmDiscard()) return;
        _filePath = null;
        _searchText = "";
        Notify(nameof(SearchText));
        LoadProfile(new LootProfile());
    }

    private async Task DoOpenAsync()
    {
        if (!ConfirmDiscard()) return;
        var opts = new FilePickerOpenOptions
        {
            Title            = "Open Loot Profile",
            AllowMultiple    = false,
            FileTypeFilter   = [new FilePickerFileType("Loot Profiles") { Patterns = ["*.json"] }],
            SuggestedStartLocation = await GetFolderAsync(),
        };
        var files = await _window.StorageProvider.OpenFilePickerAsync(opts);
        if (files.Count == 0) return;

        var path = files[0].Path.LocalPath;
        var loaded = LootProfile.TryLoad(path);
        if (loaded == null) { await ShowError("Failed to load profile."); return; }

        _filePath = path;
        _searchText = "";
        Notify(nameof(SearchText));
        LoadProfile(loaded);
        Status($"Opened {Path.GetFileName(path)}");
    }

    private async Task DoSaveAsync(bool saveAs)
    {
        if (!saveAs && _filePath != null) { Save(_filePath); return; }

        var opts = new FilePickerSaveOptions
        {
            Title                  = "Save Loot Profile",
            SuggestedFileName      = _filePath != null ? Path.GetFileNameWithoutExtension(_filePath) : "profile",
            DefaultExtension       = "json",
            FileTypeChoices        = [new FilePickerFileType("Loot Profiles") { Patterns = ["*.json"] }],
            SuggestedStartLocation = await GetFolderAsync(),
        };
        var file = await _window.StorageProvider.SaveFilePickerAsync(opts);
        if (file == null) return;
        _filePath = file.Path.LocalPath;
        Save(_filePath);
    }

    private void Save(string path)
    {
        // Flush rule list back to profile
        _profile.Rules.Clear();
        foreach (var r in Rules) _profile.Rules.Add(r);
        _profile.Name = Path.GetFileNameWithoutExtension(path);

        try
        {
            _profile.Save(path);
            _dirty = false;
            UpdateTitle();
            Status($"Saved {Path.GetFileName(path)}");
        }
        catch (Exception ex)
        {
            _ = ShowError($"Save failed:\n{ex.Message}");
        }
    }

    private async Task<IStorageFolder?> GetFolderAsync()
    {
        if (!Directory.Exists(DefaultFolder)) return null;
        return await _window.StorageProvider.TryGetFolderFromPathAsync(DefaultFolder);
    }

    // ── Rule operations ───────────────────────────────────────────────────────

    private void DoRuleAdd()
    {
        var rule = new LootRule { Name = "New Rule", Enabled = true };
        Rules.Add(rule);
        _profile.Rules.Add(rule);
        // Clear search so the new rule is visible
        _searchText = "";
        Notify(nameof(SearchText));
        ApplyFilter();
        SelectedRule = rule;
        MarkDirty();
    }

    private void DoRuleDelete()
    {
        if (_selectedRule == null) return;
        int idx = Rules.IndexOf(_selectedRule);
        Rules.Remove(_selectedRule);
        _profile.Rules.Remove(_selectedRule);
        ApplyFilter();
        SelectedRule = Rules.Count > 0 ? Rules[Math.Min(idx, Rules.Count - 1)] : null;
        MarkDirty();
    }

    private bool CanMoveRule(int dir)
    {
        if (_selectedRule == null || _searchText.Length > 0) return false;
        int idx = Rules.IndexOf(_selectedRule);
        return dir < 0 ? idx > 0 : idx < Rules.Count - 1;
    }

    private void DoRuleMove(int dir)
    {
        if (_selectedRule == null) return;
        int idx = Rules.IndexOf(_selectedRule);
        int newIdx = idx + dir;
        if (newIdx < 0 || newIdx >= Rules.Count) return;
        Rules.Move(idx, newIdx);
        _profile.Rules.RemoveAt(idx);
        _profile.Rules.Insert(newIdx, _selectedRule);
        ApplyFilter();
        MarkDirty();
    }

    // ── Condition operations ─────────────────────────────────────────────────

    private void RebuildConditions()
    {
        Conditions.Clear();
        if (_selectedRule == null) return;
        foreach (var c in _selectedRule.Conditions) Conditions.Add(c);
        SelectedCondition = null;
    }

    private void DoCondAdd()
    {
        if (_selectedRule == null) return;
        var cond = new LongValKeyGECondition();
        _selectedRule.Conditions.Add(cond);
        Conditions.Add(cond);
        SelectedCondition = cond;
        MarkDirty();
    }

    private void DoCondDelete()
    {
        if (_selectedRule == null || _selectedCondition == null) return;
        int idx = Conditions.IndexOf(_selectedCondition);
        _selectedRule.Conditions.Remove(_selectedCondition);
        Conditions.Remove(_selectedCondition);
        SelectedCondition = Conditions.Count > 0 ? Conditions[Math.Min(idx, Conditions.Count - 1)] : null;
        MarkDirty();
    }

    private bool CanMoveCond(int dir)
    {
        if (_selectedCondition == null) return false;
        int idx = Conditions.IndexOf(_selectedCondition);
        return dir < 0 ? idx > 0 : idx < Conditions.Count - 1;
    }

    private void DoCondMove(int dir)
    {
        if (_selectedRule == null || _selectedCondition == null) return;
        int idx = Conditions.IndexOf(_selectedCondition);
        int newIdx = idx + dir;
        if (newIdx < 0 || newIdx >= Conditions.Count) return;
        Conditions.Move(idx, newIdx);
        _selectedRule.Conditions.RemoveAt(idx);
        _selectedRule.Conditions.Insert(newIdx, _selectedCondition);
        MarkDirty();
    }

    // ── Condition type swap ───────────────────────────────────────────────────

    private bool _replacingCondition;

    public void ReplaceCondition(LootCondition oldCond, LootCondition newCond)
    {
        if (_replacingCondition) return;
        _replacingCondition = true;
        try
        {
            if (_selectedRule == null) return;
            int idx = _selectedRule.Conditions.IndexOf(oldCond);
            if (idx < 0) return;
            _selectedRule.Conditions[idx] = newCond;
            Conditions[Conditions.IndexOf(oldCond)] = newCond;
            _selectedCondition = newCond;
            Notify(nameof(SelectedCondition));
            RebuildCondEditor();
            MarkDirty();
        }
        finally
        {
            _replacingCondition = false;
        }
    }

    public void NotifyConditionChanged() => MarkDirty();

    // ── Condition editor ──────────────────────────────────────────────────────

    private bool _rebuildingEditor;

    private void RebuildCondEditor()
    {
        if (_rebuildingEditor) return;
        _rebuildingEditor = true;
        try
        {
            if (_selectedCondition == null)
            {
                ConditionEditor = new Avalonia.Controls.TextBlock
                {
                    Text       = "Select a condition to edit",
                    Foreground = Avalonia.Media.Brushes.Gray,
                };
                return;
            }
            ConditionEditor = new ConditionEditorView(_selectedCondition, this);
        }
        finally
        {
            _rebuildingEditor = false;
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private void MarkDirty() { _dirty = true; UpdateTitle(); }

    private void UpdateTitle()
    {
        string name = _filePath != null ? Path.GetFileName(_filePath) : "Untitled";
        _window.Title = $"{(_dirty ? "* " : "")}{name} — RynthCore Loot Editor";
    }

    private void Status(string msg) => StatusMessage = msg;

    public bool IsDirty => _dirty;

    private bool ConfirmDiscard()
    {
        // Avalonia doesn't have a synchronous MessageBox — use the window close guard
        return !_dirty; // for now; closing will prompt via OnClosing
    }

    private async Task ShowError(string msg)
    {
        var dlg = new Avalonia.Controls.Window
        {
            Title   = "Error",
            Width   = 360, Height = 140,
            Content = new Avalonia.Controls.TextBlock { Text = msg, Margin = new Avalonia.Thickness(16), TextWrapping = Avalonia.Media.TextWrapping.Wrap },
        };
        await dlg.ShowDialog(_window);
    }
}

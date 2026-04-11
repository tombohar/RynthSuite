using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;

namespace RynthCore.MonsterEditor;

public partial class MainWindow : Window
{
    // Exposed to XAML via DataContext = this
    public ObservableCollection<MonsterRule> Rules        { get; } = new();
    public List<WeaponOption>                WeaponOptions { get; } = new();

    private string _charFolder   = string.Empty;
    private string _monstersPath = string.Empty;

    // Singleton converters referenced by XAML
    public static readonly IdToNameConverter      IdToName      = IdToNameConverter.Instance;
    public static readonly InvertBoolToVisConverter InvBoolToVis = InvertBoolToVisConverter.Instance;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;

        string[] args = Environment.GetCommandLineArgs();
        if (args.Length > 1)
            OpenCharFolder(args[1].Trim('"'));
        else
            PromptOpenFile();
    }

    // ── Open / Load ──────────────────────────────────────────────────────────

    private void PromptOpenFile()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title  = "Open monsters.json",
            Filter = "Monster Rules (monsters.json)|monsters.json|All Files (*.*)|*.*"
        };
        if (dlg.ShowDialog() == true)
            OpenCharFolder(Path.GetDirectoryName(dlg.FileName)!);
    }

    private void OpenCharFolder(string charFolder)
    {
        _charFolder   = charFolder;
        _monstersPath = Path.Combine(charFolder, "monsters.json");
        Title = $"RynthAi — Monster Rules — {Path.GetFileName(charFolder)}";

        LoadWeaponOptions();
        LoadRules();
    }

    private void LoadWeaponOptions()
    {
        WeaponOptions.Clear();
        WeaponOptions.Add(new WeaponOption { Id = 0, DisplayName = "<AUTO>" });

        // Read ItemRules from the active profile JSON
        try
        {
            string markerPath = Path.Combine(_charFolder, "active_profile.txt");
            string profile    = File.Exists(markerPath) ? File.ReadAllText(markerPath).Trim() : "Default";
            string profilePath = Path.Combine(_charFolder, profile + ".json");

            if (File.Exists(profilePath))
            {
                string json     = File.ReadAllText(profilePath);
                var    settings = JsonSerializer.Deserialize(json, EditorJsonContext.Default.EditorSettings);
                if (settings?.ItemRules != null)
                {
                    foreach (var item in settings.ItemRules)
                        WeaponOptions.Add(new WeaponOption { Id = item.Id, DisplayName = item.Name });
                }
            }
        }
        catch { /* weapon dropdowns will just show <AUTO> */ }

        // Make converter aware of current options
        IdToNameConverter.Instance.Options = WeaponOptions;
    }

    private void LoadRules()
    {
        // Unsubscribe old rules
        foreach (var r in Rules) r.PropertyChanged -= Rule_Changed;
        Rules.Clear();

        if (File.Exists(_monstersPath))
        {
            try
            {
                string json  = File.ReadAllText(_monstersPath);
                var    rules = JsonSerializer.Deserialize(json, EditorJsonContext.Default.ListMonsterRule);
                if (rules != null)
                    foreach (var r in rules) AddRule(r);
            }
            catch (Exception ex) { SetStatus($"Load error: {ex.Message}", error: true); return; }
        }

        EnsureDefault();

        SetStatus(Rules.Count > 0
            ? $"Loaded {Rules.Count} rule(s) from {_monstersPath}"
            : $"No monsters.json found — starting empty. Saving will create {_monstersPath}");
    }

    /// <summary>Ensures exactly one Default rule exists at index 0.</summary>
    private void EnsureDefault()
    {
        // Remove any Default rules that aren't at index 0 (duplicates)
        for (int i = Rules.Count - 1; i >= 1; i--)
            if (Rules[i].IsDefault) { Rules[i].PropertyChanged -= Rule_Changed; Rules.RemoveAt(i); }

        if (Rules.Count > 0 && Rules[0].IsDefault)
            return; // already correct

        // No Default at index 0 — check if one exists elsewhere and move it, or create one
        int existing = -1;
        for (int i = 0; i < Rules.Count; i++)
            if (Rules[i].IsDefault) { existing = i; break; }

        if (existing > 0)
            Rules.Move(existing, 0);
        else
            Rules.Insert(0, new MonsterRule { Name = "Default", Priority = 1 });

        // Make sure the Default (wherever it ended up) has a PropertyChanged subscription
        Rules[0].PropertyChanged -= Rule_Changed;
        Rules[0].PropertyChanged += Rule_Changed;
    }

    private void AddRule(MonsterRule rule)
    {
        rule.PropertyChanged += Rule_Changed;
        Rules.Add(rule);
    }

    // ── Save ─────────────────────────────────────────────────────────────────

    private void Rule_Changed(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        => Save();

    private void MonsterGrid_CellEditEnding(object? sender, DataGridCellEditEndingEventArgs e)
    {
        // Combo/text columns commit on edit-end — save after the binding updates
        Dispatcher.InvokeAsync(Save, System.Windows.Threading.DispatcherPriority.Background);
    }

    private void Save()
    {
        if (string.IsNullOrEmpty(_monstersPath)) return;
        try
        {
            Directory.CreateDirectory(_charFolder);
            string json = JsonSerializer.Serialize(new List<MonsterRule>(Rules), EditorJsonContext.Default.ListMonsterRule);
            File.WriteAllText(_monstersPath, json);
            SetStatus($"Saved {Rules.Count} rule(s) — {DateTime.Now:HH:mm:ss}");
        }
        catch (Exception ex) { SetStatus($"Save error: {ex.Message}", error: true); }
    }

    // ── Row actions ───────────────────────────────────────────────────────────

    private void AddMonster_Click(object sender, RoutedEventArgs e)
    {
        string name = NewMonsterBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(name)) return;

        var rule = new MonsterRule { Name = name };
        AddRule(rule);
        MonsterGrid.ScrollIntoView(rule);
        MonsterGrid.SelectedItem = rule;
        NewMonsterBox.Clear();
        Save();
    }

    private void NewMonsterBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) AddMonster_Click(sender, e);
    }

    private void DeleteRow_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: MonsterRule rule } && !rule.IsDefault)
        {
            rule.PropertyChanged -= Rule_Changed;
            Rules.Remove(rule);
            Save();
        }
    }

    // ── Click handling for toggle lights ─────────────────────────────────────
    // ToggleButtons inside a DataGrid cell need one click to select the row,
    // then one click to toggle. We intercept PreviewMouseDown so the first click
    // both selects AND toggles.

    private void MonsterGrid_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        // Walk up from the clicked element until we hit a DataGridRow
        var dep = e.OriginalSource as DependencyObject;
        while (dep != null && dep is not DataGridRow)
            dep = System.Windows.Media.VisualTreeHelper.GetParent(dep);

        if (dep is DataGridRow row && !row.IsSelected)
        {
            row.IsSelected = true;
            // If the actual click target is a ToggleButton, let it process normally
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private void SetStatus(string msg, bool error = false)
    {
        StatusText.Foreground = error
            ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xF3, 0x8B, 0xA8))
            : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xA6, 0xE3, 0xA1));
        StatusText.Text = msg;
    }
}

// ── Converters ────────────────────────────────────────────────────────────────

/// <summary>Converts a weapon/offhand ID to its display name using the loaded WeaponOptions.</summary>
public class IdToNameConverter : IValueConverter
{
    public static readonly IdToNameConverter Instance = new();
    public List<WeaponOption> Options { get; set; } = new();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is int id)
        {
            if (id == 0) return "<AUTO>";
            foreach (var opt in Options)
                if (opt.Id == id) return opt.DisplayName;
            return $"<ID:{id}>";
        }
        return "<AUTO>";
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>True → Collapsed, False → Visible — hides the Delete button on the Default row.</summary>
public class InvertBoolToVisConverter : IValueConverter
{
    public static readonly InvertBoolToVisConverter Instance = new();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace RynthCore.MonsterEditor;

public class MonsterRule : INotifyPropertyChanged
{
    private string _name = "New Monster";
    private int _priority = 1;
    private string _damageType = "Auto";
    private int _weaponId;
    private bool _fester, _broadside, _gravityWell, _imperil, _yield, _vuln;
    private bool _useArc, _useBolt = true, _useRing, _useStreak;
    private string _exVuln = "None";
    private int _offhandId;
    private string _petDamage = "PAuto";

    public string Name        { get => _name;        set { _name        = value; OnPropertyChanged(); } }
    public int    Priority    { get => _priority;    set { _priority    = value; OnPropertyChanged(); } }
    public string DamageType  { get => _damageType;  set { _damageType  = value; OnPropertyChanged(); } }
    public int    WeaponId    { get => _weaponId;    set { _weaponId    = value; OnPropertyChanged(); } }
    public bool   Fester      { get => _fester;      set { _fester      = value; OnPropertyChanged(); } }
    public bool   Broadside   { get => _broadside;   set { _broadside   = value; OnPropertyChanged(); } }
    public bool   GravityWell { get => _gravityWell; set { _gravityWell = value; OnPropertyChanged(); } }
    public bool   Imperil     { get => _imperil;     set { _imperil     = value; OnPropertyChanged(); } }
    public bool   Yield       { get => _yield;       set { _yield       = value; OnPropertyChanged(); } }
    public bool   Vuln        { get => _vuln;        set { _vuln        = value; OnPropertyChanged(); } }
    public bool   UseArc      { get => _useArc;      set { _useArc      = value; OnPropertyChanged(); } }
    public bool   UseBolt     { get => _useBolt;     set { _useBolt     = value; OnPropertyChanged(); } }
    public bool   UseRing     { get => _useRing;     set { _useRing     = value; OnPropertyChanged(); } }
    public bool   UseStreak   { get => _useStreak;   set { _useStreak   = value; OnPropertyChanged(); } }
    public string ExVuln      { get => _exVuln;      set { _exVuln      = value; OnPropertyChanged(); } }
    public int    OffhandId   { get => _offhandId;   set { _offhandId   = value; OnPropertyChanged(); } }
    public string PetDamage   { get => _petDamage;   set { _petDamage   = value; OnPropertyChanged(); } }

    [JsonIgnore]
    public bool IsDefault => string.Equals(_name, "Default", System.StringComparison.OrdinalIgnoreCase);

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public class ItemRule
{
    public int    Id         { get; set; }
    public string Name       { get; set; } = string.Empty;
    public string Action     { get; set; } = "Loot";
    public string Element    { get; set; } = "Slash";
    public bool   KeepBuffed { get; set; } = true;
}

/// <summary>Minimal slice of the settings profile — only what the editor needs for dropdowns.</summary>
public class EditorSettings
{
    public List<MonsterRule> MonsterRules { get; set; } = new();
    public List<ItemRule>    ItemRules    { get; set; } = new();
}

/// <summary>Used by weapon/offhand ComboBoxes: maps weapon ID → display name.</summary>
public class WeaponOption
{
    public int    Id          { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public override string ToString() => DisplayName;
}

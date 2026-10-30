using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RynthCore.MonsterEditor;

/// <summary>The editor's copy of RynthAi's MonsterRule (Plugins/RynthCore.Plugin.RynthAi/LegacyUi/
/// LegacyUiSettings.cs), read from and saved to the same monsters file. A field the plugin has and
/// this class doesn't still survives a save, through <see cref="ExtraFields"/>.</summary>
public class MonsterRule : INotifyPropertyChanged
{
    private string _name = "New Monster";
    private string _category = "";
    private string _matchExpression = "";
    private int _priority = 1;
    private string _damageType = "Auto";
    private int _weaponId;
    private bool _fester, _broadside, _gravityWell, _imperil, _yield, _vuln;
    private bool _useArc, _useBolt = true, _useRing, _useStreak, _useBlast, _useWall;
    private string _exVuln = "None";
    private int _offhandId;
    private int _preferredAmmoItemId;
    private string _petDamage = "PAuto";
    private string _kite = "Default";

    public string Name        { get => _name;        set { _name        = value; OnPropertyChanged(); } }
    /// <summary>The plugin's rule group label. Kept and editable here (before 2026-10-05 an
    /// editor save dropped it).</summary>
    public string Category    { get => _category;    set { _category    = value ?? ""; OnPropertyChanged(); } }
    /// <summary>The plugin's match expression (CombatManager.GetRuleForTarget): when set, the
    /// rule matches when the expression is true (and the name, if any, is in the target's).
    /// Empty = match by name only. Kept and editable here (before 2026-10-05 an editor save
    /// dropped it, so expression rules fell back to name-only).</summary>
    public string MatchExpression { get => _matchExpression; set { _matchExpression = value ?? ""; OnPropertyChanged(); } }
    /// <summary>Target order: a higher Priority is fought first. 0 (or less) means IGNORE: RynthAi never targets the
    /// monster, even one attacking the player, and does not wait on it.</summary>
    public int    Priority    { get => _priority;    set { _priority    = value; OnPropertyChanged(); OnPropertyChanged(nameof(PriorityText)); } }
    /// <summary>What the Priority column shows: the number, or "ign" for Priority 0 (ignore).</summary>
    [JsonIgnore]
    public string PriorityText => _priority <= 0 ? "ign" : _priority.ToString();
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
    public bool   UseBlast    { get => _useBlast;    set { _useBlast    = value; OnPropertyChanged(); } }
    public bool   UseWall     { get => _useWall;     set { _useWall     = value; OnPropertyChanged(); } }
    public string ExVuln      { get => _exVuln;      set { _exVuln      = value; OnPropertyChanged(); } }
    public int    OffhandId   { get => _offhandId;   set { _offhandId   = value; OnPropertyChanged(); } }
    /// <summary>Optional loose ammo stack id for missile combat while this rule matches (0 = auto).</summary>
    public int    PreferredAmmoItemId { get => _preferredAmmoItemId; set { _preferredAmmoItemId = value; OnPropertyChanged(); } }
    public string PetDamage   { get => _petDamage;   set { _petDamage   = value; OnPropertyChanged(); } }
    /// <summary>Pull-away kite: Default (follow DEFAULT row), Off, or PullAway. RynthAi KiteModes.</summary>
    public string Kite        { get => _kite;        set { _kite        = value ?? "Default"; OnPropertyChanged(); } }

    /// <summary>Every field in the file this class doesn't know (e.g. the plugin's CustomDebuffs,
    /// or one added later), written back unchanged.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtraFields { get; set; }

    [JsonIgnore]
    /// <summary>True for the special row named "Default" — the catch-all when no other rule matches.</summary>
    public bool IsDefault => string.Equals(_name, "Default", System.StringComparison.OrdinalIgnoreCase);

    /// <summary>Full copy (for import merge). Copies every rule field, UseBlast included.</summary>
    public MonsterRule Clone()
    {
        return new MonsterRule
        {
            Name                = _name,
            Category            = _category,
            MatchExpression     = _matchExpression,
            Priority            = _priority,
            DamageType          = _damageType,
            WeaponId            = _weaponId,
            Fester              = _fester,
            Broadside           = _broadside,
            GravityWell         = _gravityWell,
            Imperil             = _imperil,
            Yield               = _yield,
            Vuln                = _vuln,
            UseArc              = _useArc,
            UseBolt             = _useBolt,
            UseRing             = _useRing,
            UseStreak           = _useStreak,
            UseBlast            = _useBlast,
            ExVuln              = _exVuln,
            OffhandId           = _offhandId,
            PreferredAmmoItemId = _preferredAmmoItemId,
            PetDamage           = _petDamage,
            Kite                = _kite,
        };
    }

    /// <summary>Overwrites combat fields (used to merge a VT Default row into the Rynth Default row).</summary>
    public void CopyCombatSettingsFrom(MonsterRule? src)
    {
        if (src == null) return;
        // Keep Name: must stay "Default". Use public setters so the grid updates.
        Category            = src.Category;
        MatchExpression     = src.MatchExpression;
        Priority            = src.Priority;
        DamageType          = src.DamageType;
        WeaponId            = src.WeaponId;
        Fester              = src.Fester;
        Broadside           = src.Broadside;
        GravityWell         = src.GravityWell;
        Imperil             = src.Imperil;
        Yield               = src.Yield;
        Vuln                = src.Vuln;
        UseArc              = src.UseArc;
        UseBolt             = src.UseBolt;
        UseRing             = src.UseRing;
        UseStreak           = src.UseStreak;
        UseBlast            = src.UseBlast;
        ExVuln              = src.ExVuln;
        OffhandId           = src.OffhandId;
        PreferredAmmoItemId = src.PreferredAmmoItemId;
        PetDamage           = src.PetDamage;
        Kite                = src.Kite;
    }

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

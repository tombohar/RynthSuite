using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RynthCore.Loot;

// ── Actions ───────────────────────────────────────────────────────────────────

public enum LootAction
{
    Keep,
    Sell,
    Salvage,
    Read,
    KeepUpTo,   // "Keep #" — keep at most LootRule.KeepCount in inventory
}

// ── Condition base ────────────────────────────────────────────────────────────

[JsonPolymorphic(TypeDiscriminatorPropertyName = "$t")]
[JsonDerivedType(typeof(ObjectClassCondition),      "ObjectClass")]
[JsonDerivedType(typeof(LongValKeyGECondition),     "LongGE")]
[JsonDerivedType(typeof(LongValKeyLECondition),     "LongLE")]
[JsonDerivedType(typeof(LongValKeyECondition),      "LongE")]
[JsonDerivedType(typeof(LongValKeyNECondition),     "LongNE")]
[JsonDerivedType(typeof(LongValKeyFlagCondition),   "LongFlag")]
[JsonDerivedType(typeof(DoubleValKeyGECondition),   "DoubleGE")]
[JsonDerivedType(typeof(DoubleValKeyLECondition),   "DoubleLE")]
[JsonDerivedType(typeof(StringValueCondition),      "String")]
[JsonDerivedType(typeof(TotalRatingsGECondition),   "TotalRatingsGE")]
[JsonDerivedType(typeof(MinDamageGECondition),      "MinDamageGE")]
[JsonDerivedType(typeof(DamagePercentGECondition),  "DmgPctGE")]
[JsonDerivedType(typeof(CharacterSkillGECondition), "SkillGE")]
public abstract class LootCondition { }

// ── ObjectClass ───────────────────────────────────────────────────────────────

public sealed class ObjectClassCondition : LootCondition
{
    public AcObjectClass ObjectClass { get; set; }
    public override string ToString() => $"ObjectClass == {ObjectClass}";
}

// ── LongValKey ────────────────────────────────────────────────────────────────

public sealed class LongValKeyGECondition   : LootCondition { public int Key { get; set; } public int Value { get; set; } public override string ToString() => $"LongKey[{Key}] >= {Value}"; }
public sealed class LongValKeyLECondition   : LootCondition { public int Key { get; set; } public int Value { get; set; } public override string ToString() => $"LongKey[{Key}] <= {Value}"; }
public sealed class LongValKeyECondition    : LootCondition { public int Key { get; set; } public int Value { get; set; } public override string ToString() => $"LongKey[{Key}] == {Value}"; }
public sealed class LongValKeyNECondition   : LootCondition { public int Key { get; set; } public int Value { get; set; } public override string ToString() => $"LongKey[{Key}] != {Value}"; }
public sealed class LongValKeyFlagCondition : LootCondition { public int Key { get; set; } public int FlagValue { get; set; } public override string ToString() => $"LongKey[{Key}] has flag {FlagValue}"; }

// ── DoubleValKey ──────────────────────────────────────────────────────────────

public sealed class DoubleValKeyGECondition : LootCondition { public int Key { get; set; } public double Value { get; set; } public override string ToString() => $"DoubleKey[{Key}] >= {Value}"; }
public sealed class DoubleValKeyLECondition : LootCondition { public int Key { get; set; } public double Value { get; set; } public override string ToString() => $"DoubleKey[{Key}] <= {Value}"; }

// ── StringValue ───────────────────────────────────────────────────────────────

public sealed class StringValueCondition : LootCondition
{
    public int    Key     { get; set; }
    public string Pattern { get; set; } = "";
    public override string ToString() => $"StringKey[{Key}] matches \"{Pattern}\"";
}

// ── Computed ──────────────────────────────────────────────────────────────────

public sealed class TotalRatingsGECondition  : LootCondition { public int    Value { get; set; } public override string ToString() => $"TotalRatings >= {Value}"; }
public sealed class MinDamageGECondition     : LootCondition { public double Value { get; set; } public override string ToString() => $"MinDamage >= {Value}"; }
public sealed class DamagePercentGECondition : LootCondition { public double Value { get; set; } public override string ToString() => $"DamagePercent >= {Value}"; }

// ── Character ─────────────────────────────────────────────────────────────────

public sealed class CharacterSkillGECondition : LootCondition
{
    public AcSkillType Skill { get; set; }
    public int         Value { get; set; }
    public override string ToString() => $"Skill {Skill} >= {Value}";
}

// ── Rule ──────────────────────────────────────────────────────────────────────

public sealed class LootRule : System.ComponentModel.INotifyPropertyChanged
{
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    private string _name = "";
    public string Name
    {
        get => _name;
        set { _name = value; PropertyChanged?.Invoke(this, new(nameof(Name))); }
    }

    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.Never)]
    public bool       Enabled   { get; set; } = true;
    public LootAction Action    { get; set; } = LootAction.Keep;
    public int        KeepCount { get; set; } = 1;

    public List<LootCondition> Conditions { get; set; } = new();
}

// ── Profile ───────────────────────────────────────────────────────────────────

public sealed class LootProfile
{
    public string Name { get; set; } = "";

    /// <summary>Rules evaluated top-to-bottom. First match wins.</summary>
    public List<LootRule> Rules { get; set; } = new();

    // ── Persistence ──────────────────────────────────────────────────────────

    public void Save(string path)
    {
        string json = JsonSerializer.Serialize(this, LootJsonContext.Default.LootProfile);
        File.WriteAllText(path, json);
    }

    public static LootProfile Load(string path)
    {
        string json = File.ReadAllText(path);
        return JsonSerializer.Deserialize(json, LootJsonContext.Default.LootProfile)
            ?? new LootProfile();
    }

    public static LootProfile? TryLoad(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            string json = File.ReadAllText(path);
            return JsonSerializer.Deserialize(json, LootJsonContext.Default.LootProfile);
        }
        catch
        {
            return null;
        }
    }
}

// ── JSON source-gen context ───────────────────────────────────────────────────

[JsonSourceGenerationOptions(
    WriteIndented          = true,
    UseStringEnumConverter = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault)]
[JsonSerializable(typeof(LootProfile))]
public sealed partial class LootJsonContext : JsonSerializerContext { }

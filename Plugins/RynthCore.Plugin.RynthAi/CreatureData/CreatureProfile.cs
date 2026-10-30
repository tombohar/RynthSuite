using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace RynthCore.Plugin.RynthAi.CreatureData;

/// <summary>
/// Persistent record of observed creature data — vitals, resists, armor, spells.
/// Captured passively from IdentifyObject responses and QueryHealth replies.
/// Keyed by Name (with optional Wcid disambiguator) in CreatureProfileStore.
/// </summary>
public sealed class CreatureProfile
{
    public string Name { get; set; } = string.Empty;

    public uint Wcid { get; set; }

    public int CreatureType { get; set; }

    /// <summary>Max health at difficulty tier 0 (the unscaled world; every tier on servers without tiers).</summary>
    public uint MaxHealth { get; set; }

    /// <summary>
    /// Max health at difficulty tiers above 0 (a server plugin's tiers, DifficultyTier: scaled
    /// worlds or copies), tier -> highest seen. Null until one is seen, and then left out of the
    /// file on every server without tiers. Resists, armour, spells and type are the same at every tier.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<int, uint>? TierMaxHealth { get; set; }
    public uint MaxStamina { get; set; }
    public uint MaxMana { get; set; }

    public int ArmorLevel { get; set; }

    public double ResistSlash { get; set; } = 1.0;
    public double ResistPierce { get; set; } = 1.0;
    public double ResistBludgeon { get; set; } = 1.0;
    public double ResistFire { get; set; } = 1.0;
    public double ResistCold { get; set; } = 1.0;
    public double ResistAcid { get; set; } = 1.0;
    public double ResistElectric { get; set; } = 1.0;

    public List<uint> KnownSpellIds { get; set; } = new();

    public int Samples { get; set; }
    public string LastSeen { get; set; } = string.Empty;
    public string Notes { get; set; } = string.Empty;
}

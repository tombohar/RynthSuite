// AcStubs.cs — Minimal stub types for AC client interop.
// TODO: Replace each stub with a proper RynthCore implementation, one at a time.
using System;
using System.Collections.Generic;
using RynthCore.Loot; // AcObjectClass, AcSkillType live in the shared SDK

namespace RynthCore.Plugin.RynthAi;

/// <summary>
/// STypeInt keys used with TryGetObjectIntProperty / WorldObjectCache.GetIntProperty.
/// Values match Chorizite STypes.cs STypeInt sequential enum.
/// </summary>
public enum LongValueKey
{
    ItemsCapacity          = 6,   // max items a container can hold
    ContainersCapacity     = 7,   // max sub-containers a container can hold
    Locations              = 9,   // equipment slot bitmask (valid equip locations)
    CurrentWieldedLocation = 10,  // slot item is currently wielded in (0 = not wielded)
    MaxStackSize           = 11,  // max stack size (StackMax)
    StackCount             = 12,  // current stack size (StackSize)
    DamageType             = 45,  // DAMAGE_TYPE flags

    // Legacy alias kept for call-site compatibility
    EquippedSlots          = 10,
}

/// <summary>
/// STypeString keys used with TryGetObjectStringProperty / WorldObjectCache.GetStringProperty.
/// Values match Chorizite's STypeString sequential enum.
/// </summary>
public enum StringValueKey
{
    Name = 1,
    ShortDesc = 15,
    LongDesc = 16,
    PluralName = 20,
    DisplayName = 42,
}

/// <summary>
/// STypeFloat keys used with TryGetObjectDoubleProperty / WorldObjectCache.GetDoubleProperty.
/// Values match Chorizite's STypeFloat sequential enum.
/// </summary>
public enum DoubleValueKey
{
    DamageVariance = 22,
    CurrentPowerMod = 23,
    AccuracyMod = 24,
    WeaponDefense = 29,
    UseRadius = 54,
    WeaponOffense = 61,
    DamageMod = 62,
    PowerLevel = 86,
    AccuracyLevel = 87,
}

// ── Combat mode int constants (matches acclient.exe CM_Combat enum) ────────

public static class CombatMode
{
    public const int NonCombat = 1;
    public const int Melee = 2;
    public const int Missile = 4;
    public const int Magic = 8;
}

// ── WorldObject stub ───────────────────────────────────────────────────────
// TODO: Populate from OnCreateObject + TryGetObjectName/Position events

public class WorldObject
{
    public int Id { get; }
    public string Name { get; }
    public AcObjectClass ObjectClass { get; }
    public int Container => Cache?.GetContainerId(Id) ?? 0;
    public int Wielder => Cache?.GetWielderId(Id) ?? 0;
    public int WieldedLocation => _wieldedLocationDirect >= 0 ? _wieldedLocationDirect : (Cache?.GetWieldedLocation(Id) ?? 0);

    // Direct override for items discovered by lightweight scan (bypasses cache)
    internal int _wieldedLocationDirect = -1;

    public WorldObject(int id, string name, AcObjectClass objectClass = AcObjectClass.Unknown)
    {
        Id = id;
        Name = name ?? "";
        ObjectClass = objectClass;
    }

    // Set by WorldObjectCache when it creates/updates this object
    internal WorldObjectCache? Cache { get; set; }

    public int Values(LongValueKey key, int defaultValue)
        => Cache?.GetIntProperty(Id, (uint)key, defaultValue) ?? defaultValue;

    public int Values(int key, int defaultValue)
        => Cache?.GetIntProperty(Id, (uint)key, defaultValue) ?? defaultValue;

    public string Values(StringValueKey key, string defaultValue)
        => Cache?.GetStringProperty(Id, (uint)key, defaultValue) ?? defaultValue;

    public double Values(DoubleValueKey key, double defaultValue)
        => Cache?.GetDoubleProperty(Id, (uint)key, defaultValue) ?? defaultValue;
}

// ── SpellInfo stub ────────────────────────────────────────────────────────

public class SpellInfo
{
    public int Id { get; }
    public string Name { get; }

    /// <summary>
    /// Spell family — groups all tiers of the same spell (e.g. Strength Self I–VIII).
    /// Derived by stripping tier suffixes from the spell name.
    /// </summary>
    public int Family { get; }

    public SpellInfo(int id, string name)
    {
        Id = id;
        Name = name ?? "";
        Family = ComputeFamily(Name);
    }

    private static readonly string[] TierSuffixes =
    {
        " Self VIII", " Self VII", " Self VI", " Self V", " Self IV", " Self III", " Self II", " Self I",
        " Other VIII", " Other VII", " Other VI", " Other V", " Other IV", " Other III", " Other II", " Other I",
        " VIII", " VII", " VI", " V", " IV", " III", " II", " I",
    };

    /// <summary>
    /// Maps tier 7 lore names back to their base spell names so all tiers
    /// of the same spell produce the same family hash. Without this,
    /// "Inferno's Bane" (tier 7) and "Incantation of Flame Bane" (tier 8)
    /// would hash to different families, breaking timer lookups.
    /// </summary>
    private static readonly Dictionary<string, string> LoreToBase = new(StringComparer.OrdinalIgnoreCase)
    {
        // Creature buffs
        { "Might of the Lugians", "Strength" },
        { "Preservance", "Endurance" }, { "Perseverance", "Endurance" },
        { "Honed Control", "Coordination" },
        { "Hastening", "Quickness" },
        { "Inner Calm", "Focus" },
        { "Mind Blossom", "Willpower" },
        { "Aura of Defense", "Invulnerability" },
        { "Aura of Deflection", "Impregnability" },
        { "Aura of Resistance", "Magic Resistance" },
        // Life protections
        { "Executor's Blessing", "Armor" },
        { "Caustic Blessing", "Acid Protection" },
        { "Blessing of the Blade Turner", "Blade Protection" },
        { "Blessing of the Mace Turner", "Bludgeoning Protection" },
        { "Icy Blessing", "Cold Protection" },
        { "Fiery Blessing", "Fire Protection" },
        { "Storm's Blessing", "Lightning Protection" },
        { "Blessing of the Arrow Turner", "Piercing Protection" },
        { "Battlemage's Blessing", "Mana Renewal" },
        { "Robustify", "Regeneration" },
        { "Unflinching Persistence", "Rejuvenation" }, { "Unfinching Persistance", "Rejuvenation" },
        { "Adja's Intervention", "Heal" },
        { "Robustification", "Revitalize" },
        { "Meditative Trance", "Stamina to Mana" },
        { "Rushed Recovery", "Stamina to Health" },
        // Skill masteries
        { "Topheron's Blessing", "Monster Attunement" },
        { "Kaluhc's Blessing", "Person Attunement" },
        { "Aliester's Blessing", "Arcane Enlightenment" },
        { "Jibril's Blessing", "Armor Tinkering Expertise" },
        { "Yoshi's Blessing", "Item Tinkering Expertise" },
        { "Koga's Blessing", "Weapon Tinkering Expertise" },
        { "Nuhmidira's Blessing", "Mana Conversion Mastery" },
        { "Saladur's Blessing", "Sprint" },
        { "Jahannan's Blessing", "Jumping Mastery" },
        { "Odif's Blessing", "Fealty" }, { "Odif's Boon", "Fealty" },
        { "Ar-Pei's Blessing", "Leadership Mastery" },
        { "Ketnan's Blessing", "Deception Mastery" },
        { "Avalenne's Blessing", "Healing Mastery" },
        { "Oswald's Blessing", "Lockpick Mastery" },
        { "Morimoto's Blessing", "Cooking Mastery" },
        { "Lilitha's Blessing", "Fletching Mastery" },
        { "Silencia's Blessing", "Alchemy Mastery" },
        { "Adja's Blessing", "Creature Enchantment Mastery" },
        { "Celcynd's Blessing", "Item Enchantment Mastery" },
        { "Harlune's Blessing", "Life Magic Mastery" },
        { "Hieromancer's Blessing", "War Magic Mastery" },
        // Weapon auras
        { "Aura of Infected Caress", "Blood Drinker" },
        { "Aura of Mystic's Blessing", "Hermetic Link" },
        { "Aura of Elysa's Sight", "Heart Seeker" },
        { "Aura of Infected Spirit Carress", "Spirit Drinker" },
        { "Aura of Infected Spirit Caress", "Spirit Drinker" },
        { "Aura of Atlan's Alacrity", "Swift Killer" },
        { "Aura of Cragstone's Will", "Defender" },
        // Armor banes
        { "Brogard's Defiance", "Impenetrability" },
        { "Olthoi's Bane", "Acid Bane" },
        { "Swordsman's Bane", "Blade Bane" }, { "Swordman's Bane", "Blade Bane" },
        { "Tusker's Bane", "Bludgeoning Bane" },
        { "Inferno's Bane", "Flame Bane" },
        { "Gelidite's Bane", "Frost Bane" },
        { "Astyrrian's Bane", "Lightning Bane" },
        { "Archer's Bane", "Piercing Bane" },
    };

    private static int ComputeFamily(string spellName)
    {
        string n = spellName;

        // 1. Strip tier suffixes (e.g. " Self VII", " VIII")
        foreach (var s in TierSuffixes)
        {
            if (n.EndsWith(s, StringComparison.OrdinalIgnoreCase))
            { n = n[..^s.Length].Trim(); break; }
        }

        // 2. Map tier 7 lore names to base names BEFORE stripping prefixes,
        //    since lore names like "Aura of Infected Caress" include the prefix.
        if (LoreToBase.TryGetValue(n, out string? baseName))
            n = baseName;

        // 3. Strip "Incantation of " and "Aura of " prefixes (handles
        //    "Aura of Incantation of X Self" layered prefix forms)
        bool changed = true;
        while (changed)
        {
            changed = false;
            if (n.StartsWith("Incantation of ", StringComparison.OrdinalIgnoreCase))
            { n = n[15..].Trim(); changed = true; }
            if (n.StartsWith("Aura of ", StringComparison.OrdinalIgnoreCase))
            { n = n[8..].Trim(); changed = true; }
        }

        // 4. Strip " Self" / " Other" target suffixes, then lowercase
        n = n.Replace(" Self", "").Replace(" Other", "").Trim().ToLowerInvariant();

        // FNV-1a — deterministic across sessions (String.GetHashCode is randomized in .NET 5+)
        unchecked
        {
            int hash = (int)2166136261u;
            foreach (char c in n)
                hash = (hash ^ c) * 16777619;
            return (int)((uint)hash & 0x7FFFFFFF); // strip sign bit, keep positive
        }
    }
}

// ── SpellTable stub ────────────────────────────────────────────────────────
// Backed by SpellDatabase (embedded SpellData.txt)

public static class SpellTableStub
{
    public static SpellInfo? GetById(int spellId)
    {
        string name = SpellDatabase.GetSpellNameOrId(spellId);
        // GetSpellNameOrId returns the id as a string if not found
        if (name == spellId.ToString()) return null;
        return new SpellInfo(spellId, name);
    }
}

// ── Character skills ───────────────────────────────────────────────────────
// Reads live skill data via CACQualities::InqSkillLevel / InqSkillAdvancementClass.
// SKILL_ADVANCEMENT_CLASS: UNDEF=0, UNTRAINED=1, TRAINED=2, SPECIALIZED=3.

public class CharacterSkillInfo
{
    public int Training { get; }
    public int Buffed { get; }

    public CharacterSkillInfo(int training, int buffed)
    {
        Training = training;
        Buffed   = buffed;
    }
}

public class CharacterSkills
{
    private readonly RynthCore.PluginSdk.RynthCoreHost _host;
    private uint _playerId;

    public CharacterSkills(RynthCore.PluginSdk.RynthCoreHost host)
    {
        _host = host;
    }

    public void SetPlayerId(uint playerId) => _playerId = playerId;

    public CharacterSkillInfo this[AcSkillType skill]
    {
        get
        {
            if (_playerId == 0 || !_host.HasGetObjectSkill)
                return new CharacterSkillInfo(2, 250); // fallback stubs

            uint stype = AcSkillTypeToSTypeSkill(skill);
            if (stype == 0)
                return new CharacterSkillInfo(2, 250);

            if (_host.TryGetObjectSkill(_playerId, stype, out int buffed, out int training))
                return new CharacterSkillInfo(training, buffed);

            return new CharacterSkillInfo(0, 0);
        }
    }

    /// <summary>
    /// Maps AcSkillType to AC's internal STypeSkill sequential enum values
    /// (from Chorizite STypes.cs STypeSkill enum, 0-based sequential).
    /// </summary>
    public static uint AcSkillTypeToSTypeSkill(AcSkillType skill) => skill switch
    {
        AcSkillType.MeleeDefense         => 6,
        AcSkillType.MissileDefense       => 7,
        AcSkillType.MagicDefense         => 15,
        AcSkillType.ItemEnchantment      => 32,
        AcSkillType.LifeMagic            => 33,
        AcSkillType.CreatureEnchantment  => 31,
        AcSkillType.WarMagic             => 34,
        AcSkillType.VoidMagic            => 43,
        AcSkillType.HeavyWeapons         => 44,
        AcSkillType.LightWeapons         => 45,
        AcSkillType.FinesseWeapons       => 46,
        AcSkillType.TwoHandedCombat      => 41,
        AcSkillType.Shield               => 48,
        AcSkillType.DualWield            => 49,
        AcSkillType.Recklessness         => 50,
        AcSkillType.SneakAttack          => 51,
        AcSkillType.DirtyFighting        => 52,
        AcSkillType.ArcaneLore           => 14,
        AcSkillType.ArmorTinkering       => 29,
        AcSkillType.ItemTinkering        => 18,
        AcSkillType.MagicItemTinkering   => 30,
        AcSkillType.WeaponTinkering      => 28,
        AcSkillType.Salvaging            => 40,
        AcSkillType.Run                  => 24,
        AcSkillType.Jump                 => 22,
        AcSkillType.Loyalty              => 36,
        AcSkillType.Leadership           => 35,
        AcSkillType.Deception            => 20,
        AcSkillType.Healing              => 21,
        AcSkillType.Lockpick             => 23,
        AcSkillType.Cooking              => 39,
        AcSkillType.Fletching            => 37,
        AcSkillType.Alchemy              => 38,
        AcSkillType.ManaConversion       => 16,
        AcSkillType.AssessCreature       => 27,
        AcSkillType.AssessPerson         => 19,
        AcSkillType.Summoning            => 54,
        _                                => 0,
    };
}

// ── Enchantment stub ───────────────────────────────────────────────────────

public class EnchantmentInfo
{
    public int SpellId { get; }
    public double TimeRemaining { get; }
    public EnchantmentInfo(int spellId, double timeRemaining) { SpellId = spellId; TimeRemaining = timeRemaining; }
}

// ── Player vitals cache ────────────────────────────────────────────────────
// Updated from OnUpdateHealth + periodic TryGetPlayerVitals polls

public class PlayerVitalsCache
{
    public uint MaxHealth { get; set; }
    public uint CurrentHealth { get; set; }
    public uint MaxMana { get; set; }
    public uint CurrentMana { get; set; }
    public uint MaxStamina { get; set; }
    public uint CurrentStamina { get; set; }

    public int HealthPct => MaxHealth > 0 ? (int)(CurrentHealth * 100u / MaxHealth) : 100;
    public int ManaPct => MaxMana > 0 ? (int)(CurrentMana * 100u / MaxMana) : 100;
    public int StaminaPct => MaxStamina > 0 ? (int)(CurrentStamina * 100u / MaxStamina) : 100;
}

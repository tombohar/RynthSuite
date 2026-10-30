// MagItemInfoTables.cs — name tables for the Mag-style item info line (MagItemDescriber).
// Skill / mastery / set / material names follow UtilityBelt ub-IT (Lib/ItemInfoHelper/Dictionaries.cs,
// itself from Mag-Tools / Alinco / VTank). Slayer names use the full ACE CreatureType enum instead of
// ub-IT's 17-entry subset so every slayer resolves.
using System.Collections.Generic;
using System.Text;

namespace RynthCore.Plugin.RynthAi.ItemInfo;

internal static class MagItemInfoTables
{
    /// <summary>Skill id → name (wield / activation / summoning requirements).</summary>
    public static readonly Dictionary<int, string> Skills = new()
    {
        { 0x01, "Axe" }, { 0x02, "Bow" }, { 0x03, "Crossbow" }, { 0x04, "Dagger" }, { 0x05, "Mace" },
        { 0x06, "Melee Defense" }, { 0x07, "Missile Defense" }, { 0x08, "Sling" }, { 0x09, "Spear" },
        { 0x0A, "Staff" }, { 0x0B, "Sword" }, { 0x0C, "Thrown Weapons" }, { 0x0D, "Unarmed Combat" },
        { 0x0E, "Arcane Lore" }, { 0x0F, "Magic Defense" }, { 0x10, "Mana Conversion" },
        { 0x12, "Item Tinkering" }, { 0x13, "Assess Person" }, { 0x14, "Deception" }, { 0x15, "Healing" },
        { 0x16, "Jump" }, { 0x17, "Lockpick" }, { 0x18, "Run" }, { 0x1B, "Assess Creature" },
        { 0x1C, "Weapon Tinkering" }, { 0x1D, "Armor Tinkering" }, { 0x1E, "Magic Item Tinkering" },
        { 0x1F, "Creature Enchantment" }, { 0x20, "Item Enchantment" }, { 0x21, "Life Magic" },
        { 0x22, "War Magic" }, { 0x23, "Leadership" }, { 0x24, "Loyalty" }, { 0x25, "Fletching" },
        { 0x26, "Alchemy" }, { 0x27, "Cooking" }, { 0x28, "Salvaging" }, { 0x29, "Two Handed Combat" },
        { 0x2A, "Gearcraft" }, { 0x2B, "Void" }, { 0x2C, "Heavy Weapons" }, { 0x2D, "Light Weapons" },
        { 0x2E, "Finesse Weapons" }, { 0x2F, "Missile Weapons" }, { 0x30, "Shield" }, { 0x31, "Dual Wield" },
        { 0x32, "Recklessness" }, { 0x33, "Sneak Attack" }, { 0x34, "Dirty Fighting" }, { 0x35, "Challenge" },
        { 0x36, "Summoning" },
    };

    /// <summary>ACE PropertyInt.WeaponType (353) → mastery name.</summary>
    public static readonly Dictionary<int, string> Masteries = new()
    {
        { 1, "Unarmed Weapon" }, { 2, "Sword" }, { 3, "Axe" }, { 4, "Mace" }, { 5, "Spear" }, { 6, "Dagger" },
        { 7, "Staff" }, { 8, "Bow" }, { 9, "Crossbow" }, { 10, "Thrown" }, { 11, "Two Handed Combat" }, { 12, "Wand" },
    };

    /// <summary>ACE PropertyInt.MaterialType (131) → material name.</summary>
    public static readonly Dictionary<int, string> Materials = new()
    {
        { 1, "Ceramic" }, { 2, "Porcelain" }, { 4, "Linen" }, { 5, "Satin" }, { 6, "Silk" }, { 7, "Velvet" },
        { 8, "Wool" }, { 10, "Agate" }, { 11, "Amber" }, { 12, "Amethyst" }, { 13, "Aquamarine" },
        { 14, "Azurite" }, { 15, "Bag of Abyssal-Touched" }, { 16, "Black Opal" }, { 17, "Bloodstone" },
        { 18, "Carnelian" }, { 19, "Citrine" }, { 20, "Diamond" }, { 21, "Emerald" }, { 22, "Fire Opal" },
        { 23, "Green Garnet" }, { 24, "Green Jade" }, { 25, "Hematite" }, { 26, "Imperial Topaz" }, { 27, "Jet" },
        { 28, "Lapis Lazuli" }, { 29, "Lavender Jade" }, { 30, "Malachite" }, { 31, "Moonstone" }, { 32, "Onyx" },
        { 33, "Opal" }, { 34, "Peridot" }, { 35, "Red Garnet" }, { 36, "Red Jade" }, { 37, "Rose Quartz" },
        { 38, "Ruby" }, { 39, "Sapphire" }, { 40, "Smokey Quartz" }, { 41, "Sunstone" }, { 42, "Tiger Eye" },
        { 43, "Tourmaline" }, { 44, "Turquoise" }, { 45, "White Jade" }, { 46, "White Quartz" },
        { 47, "White Sapphire" }, { 48, "Yellow Garnet" }, { 49, "Yellow Topaz" }, { 50, "Zircon" },
        { 51, "Ivory" }, { 52, "Leather" }, { 53, "Armoredillo Hide" }, { 54, "Gromnie Hide" },
        { 55, "Reed Shark Hide" }, { 57, "Brass" }, { 58, "Bronze" }, { 59, "Copper" }, { 60, "Gold" },
        { 61, "Iron" }, { 62, "Pyreal" }, { 63, "Silver" }, { 64, "Steel" }, { 66, "Alabaster" },
        { 67, "Granite" }, { 68, "Marble" }, { 69, "Obsidian" }, { 70, "Sandstone" }, { 71, "Serpentine" },
        { 73, "Ebony" }, { 74, "Mahogany" }, { 75, "Oak" }, { 76, "Pine" }, { 77, "Teak" },
    };

    /// <summary>ACE PropertyInt.EquipmentSetId (265) → set name.</summary>
    public static readonly Dictionary<int, string> EquipmentSets = BuildSets();

    /// <summary>ACE CreatureType enum, index = value (0..101). Used for SlayerCreatureType (166).</summary>
    private static readonly string[] CreatureTypes =
    {
        "Invalid", "Olthoi", "Banderling", "Drudge", "Mosswart", "Lugian", "Tumerok", "Mite", "Tusker",
        "Phyntos Wasp", "Rat", "Auroch", "Cow", "Golem", "Undead", "Gromnie", "Reedshark", "Armoredillo",
        "Fae", "Virindi", "Wisp", "Knathtead", "Shadow", "Mattekar", "Mumiyah", "Rabbit", "Sclavus",
        "Shallows Shark", "Monouga", "Zefir", "Skeleton", "Human", "Shreth", "Chittick", "Moarsman",
        "Olthoi Larvae", "Slithis", "Deru", "Fire Elemental", "Snowman", "Unknown", "Bunny",
        "Lightning Elemental", "Rockslide", "Grievver", "Niffis", "Ursuin", "Crystal", "Hollow Minion",
        "Scarecrow", "Idol", "Empyrean", "Hopeslayer", "Doll", "Marionette", "Carenzi", "Siraluun",
        "Aun Tumerok", "Hea Tumerok", "Simulacrum", "Acid Elemental", "Frost Elemental", "Elemental",
        "Statue", "Wall", "Altered Human", "Device", "Harbinger", "Dark Sarcophagus", "Chicken",
        "Gotrok Lugian", "Margul", "Bleached Rabbit", "Nasty Rabbit", "Grimacing Rabbit", "Burun", "Target",
        "Ghost", "Fiun", "Eater", "Penguin", "Ruschk", "Thrungus", "Viamontian Knight", "Remoran", "Swarm",
        "Moar", "Enchanted Arms", "Sleech", "Mukkir", "Merwart", "Food", "Paradox Olthoi", "Harvest",
        "Energy", "Apparition", "Aerbax", "Touched", "Blighted Moarsman", "Gear Knight", "Gurog", "A'nekshay",
    };

    /// <summary>Slayer creature name, or null for 0 / unknown values.</summary>
    public static string? CreatureTypeName(int value) =>
        value > 0 && value < CreatureTypes.Length ? CreatureTypes[value] : null;

    /// <summary>
    /// ACE DamageType flags (45) → "Slash", "Slash/Pierce", "Lightning" … Empty when no known bit is set.
    /// </summary>
    public static string DamageTypeName(int flags)
    {
        if (flags <= 0) return string.Empty;
        var sb = new StringBuilder();
        void Add(int bit, string name)
        {
            if ((flags & bit) == 0) return;
            if (sb.Length > 0) sb.Append('/');
            sb.Append(name);
        }
        Add(0x001, "Slash");
        Add(0x002, "Pierce");
        Add(0x004, "Bludgeon");
        Add(0x008, "Cold");
        Add(0x010, "Fire");
        Add(0x020, "Acid");
        Add(0x040, "Lightning");
        Add(0x400, "Nether");
        return sb.ToString();
    }

    private static Dictionary<int, string> BuildSets()
    {
        var d = new Dictionary<int, string>
        {
            { 2, "Test" }, { 4, "Carraida's Benediction" }, { 5, "Noble Relic Set" }, { 6, "Ancient Relic Set" },
            { 7, "Relic Alduressa Set" }, { 8, "Shou-jen Set" }, { 9, "Empyrean Rings Set" },
            { 10, "Arm, Mind, Heart Set" }, { 11, "Coat of the Perfect Light Set" },
            { 12, "Leggings of Perfect Light Set" }, { 13, "Soldier's Set" }, { 14, "Adept's Set" },
            { 15, "Archer's Set" }, { 16, "Defender's Set" }, { 17, "Tinker's Set" }, { 18, "Crafter's Set" },
            { 19, "Hearty Set" }, { 20, "Dexterous Set" }, { 21, "Wise Set" }, { 22, "Swift Set" },
            { 23, "Hardened Set" }, { 24, "Reinforced Set" }, { 25, "Interlocking Set" },
            { 26, "Flame Proof Set" }, { 27, "Acid Proof Set" }, { 28, "Cold Proof Set" },
            { 29, "Lightning Proof Set" }, { 30, "Dedication Set" }, { 31, "Gladiatorial Clothing Set" },
            { 32, "Ceremonial Clothing" }, { 33, "Protective Clothing" }, { 34, "Noobie Armor" },
            { 35, "Sigil of Defense" }, { 36, "Sigil of Destruction" }, { 37, "Sigil of Fury" },
            { 38, "Sigil of Growth" }, { 39, "Sigil of Vigor" }, { 40, "Heroic Protector Set" },
            { 41, "Heroic Destroyer Set" }, { 42, "Olthoi Armor D Red" }, { 43, "Olthoi Armor C Rat" },
            { 44, "Olthoi Armor C Red" }, { 45, "Olthoi Armor D Rat" }, { 46, "Upgraded Relic Alduressa Set" },
            { 47, "Upgraded Ancient Relic Set" }, { 48, "Upgraded Noble Relic Set" },
            { 49, "Weave of Alchemy" }, { 50, "Weave of Arcane Lore" }, { 51, "Weave of Armor Tinkering" },
            { 52, "Weave of Assess Person" }, { 53, "Weave of Light Weapons" }, { 54, "Weave of Missile Weapons" },
            { 55, "Weave of Cooking" }, { 56, "Weave of Creature Enchantment" }, { 57, "Weave of Missile Weapons" },
            { 58, "Weave of Finesse" }, { 59, "Weave of Deception" }, { 60, "Weave of Fletching" },
            { 61, "Weave of Healing" }, { 62, "Weave of Item Enchantment" }, { 63, "Weave of Item Tinkering" },
            { 64, "Weave of Leadership" }, { 65, "Weave of Life Magic" }, { 66, "Weave of Loyalty" },
            { 67, "Weave of Light Weapons" }, { 68, "Weave of Magic Defense" },
            { 69, "Weave of Magic Item Tinkering" }, { 70, "Weave of Mana Conversion" },
            { 71, "Weave of Melee Defense" }, { 72, "Weave of Missile Defense" }, { 73, "Weave of Salvaging" },
            { 74, "Weave of Light Weapons" }, { 75, "Weave of Light Weapons" }, { 76, "Weave of Heavy Weapons" },
            { 77, "Weave of Missile Weapons" }, { 78, "Weave of Two Handed Combat" }, { 79, "Weave of Light Weapons" },
            { 80, "Weave of Void Magic" }, { 81, "Weave of War Magic" }, { 82, "Weave of Weapon Tinkering" },
            { 83, "Weave of Assess Creature" }, { 84, "Weave of Dirty Fighting" }, { 85, "Weave of Dual Wield" },
            { 86, "Weave of Recklessness" }, { 87, "Weave of Shield" }, { 88, "Weave of Sneak Attack" },
            { 89, "Ninja_New" }, { 90, "Weave of Summoning" }, { 91, "Shrouded Soul" }, { 92, "Darkened Mind" },
            { 93, "Clouded Spirit" }, { 130, "Shimmering Shadows" }, { 131, "Brown Society Locket" },
            { 132, "Yellow Society Locket" }, { 133, "Red Society Band" }, { 134, "Green Society Band" },
            { 135, "Purple Society Band" }, { 136, "Blue Society Band" }, { 137, "Gauntlet Garb" },
            { 138, "Paragon" }, { 139, "Paragon" }, { 140, "Paragon" },
        };

        // 94..129: Minor / Major / Blackfire × Stinging / Sparking / Smoldering / Shivering ×
        // Shrouded Soul / Darkened Mind / Clouded Spirit (same order as the VTank set list).
        string[] tiers = { "Minor", "Major", "Blackfire" };
        string[] souls = { "Shrouded Soul", "Darkened Mind", "Clouded Spirit" };
        string[] elements = { "Stinging", "Sparking", "Smoldering", "Shivering" };
        int id = 94;
        foreach (string tier in tiers)
            foreach (string soul in souls)
                foreach (string element in elements)
                    d[id++] = $"{tier} {element} {soul}";
        return d;
    }
}

using System;
using System.Collections.Generic;
using System.Text;

namespace RynthCore.Plugin.RynthInventory.Data;

/// <summary>The category filter groups. Stored by name in the files.</summary>
internal static class ItemCategory
{
    public const string Weapon = "weapon";
    public const string Armor = "armor";
    public const string Clothing = "clothing";
    public const string Jewelry = "jewelry";
    public const string Caster = "caster";
    public const string Salvage = "salvage";
    public const string Components = "components";
    public const string Gem = "gem";
    public const string ManaStone = "manastone";
    public const string Money = "money";
    public const string Key = "key";
    public const string Food = "food";
    public const string Container = "container";
    public const string Crafting = "crafting";
    public const string Misc = "misc";

    /// <summary>Filter order and labels.</summary>
    public static readonly (string Id, string Label)[] All =
    {
        (Weapon, "Weapons"), (Caster, "Casters"), (Armor, "Armor"), (Clothing, "Clothing"), (Jewelry, "Jewelry"),
        (Salvage, "Salvage"), (Components, "Spell components"), (Gem, "Gems"), (ManaStone, "Mana stones"),
        (Money, "Money and notes"), (Key, "Keys"), (Food, "Food"), (Container, "Packs"), (Crafting, "Crafting"),
        (Misc, "Misc"),
    };

    public static string Label(string id)
    {
        foreach (var (i, l) in All) if (i == id) return l;
        return "Misc";
    }

    // AC ItemType flags (STypeInt 1).
    private const uint MeleeWeapon = 0x00000001, ArmorT = 0x00000002, ClothingT = 0x00000004, JewelryT = 0x00000008,
        FoodT = 0x00000020, MoneyT = 0x00000040, MissileWeapon = 0x00000100, ContainerT = 0x00000200,
        GemT = 0x00000800, SpellComponents = 0x00001000, KeyT = 0x00004000, CasterT = 0x00008000,
        PromissoryNote = 0x00040000, ManaStoneT = 0x00080000, CraftCookingBase = 0x00400000,
        CraftAlchemyBase = 0x00800000, CraftFletchingBase = 0x02000000, CraftAlchemyIntermediate = 0x04000000,
        CraftFletchingIntermediate = 0x08000000, TinkeringTool = 0x20000000, TinkeringMaterial = 0x40000000;

    public const uint ContainerFlag = ContainerT;

    /// <summary>The category for an ItemType, most specific flag first (the order RynthAi classifies in).</summary>
    public static string FromItemType(uint t)
    {
        if ((t & PromissoryNote) != 0) return Money;
        if ((t & ManaStoneT) != 0) return ManaStone;
        if ((t & SpellComponents) != 0) return Components;
        if ((t & GemT) != 0) return Gem;
        if ((t & KeyT) != 0) return Key;
        if ((t & TinkeringMaterial) != 0) return Salvage;
        if ((t & (TinkeringTool | CraftCookingBase | CraftAlchemyBase | CraftFletchingBase
                  | CraftAlchemyIntermediate | CraftFletchingIntermediate)) != 0) return Crafting;
        if ((t & ContainerT) != 0) return Container;
        if ((t & CasterT) != 0) return Caster;
        if ((t & (MissileWeapon | MeleeWeapon)) != 0) return Weapon;
        if ((t & ArmorT) != 0) return Armor;
        if ((t & ClothingT) != 0) return Clothing;
        if ((t & JewelryT) != 0) return Jewelry;
        if ((t & FoodT) != 0) return Food;
        if ((t & MoneyT) != 0) return Money;
        return Misc;
    }
}

/// <summary>Names for equip slots, materials and armor sets.</summary>
internal static class ItemNames
{
    private static readonly (uint Bit, string Name)[] SlotBits =
    {
        (0x00000001, "Head"), (0x00000002, "Chest (shirt)"), (0x00000004, "Abdomen (pants)"),
        (0x00000008, "Upper arms (shirt)"), (0x00000010, "Lower arms (shirt)"), (0x00000020, "Hands"),
        (0x00000040, "Upper legs (pants)"), (0x00000080, "Lower legs (pants)"), (0x00000100, "Feet"),
        (0x00000200, "Chest"), (0x00000400, "Abdomen"), (0x00000800, "Upper arms"), (0x00001000, "Lower arms"),
        (0x00002000, "Upper legs"), (0x00004000, "Lower legs"), (0x00008000, "Neck"),
        (0x00010000, "Left wrist"), (0x00020000, "Right wrist"), (0x00040000, "Left ring"), (0x00080000, "Right ring"),
        (0x00100000, "Melee weapon"), (0x00200000, "Shield"), (0x00400000, "Missile weapon"), (0x00800000, "Ammunition"),
        (0x01000000, "Held"), (0x02000000, "Two-handed"), (0x04000000, "Trinket"), (0x08000000, "Cloak"),
        (0x10000000, "Blue aetheria"), (0x20000000, "Yellow aetheria"), (0x40000000, "Red aetheria"),
    };

    /// <summary>"Chest, Upper arms" for a multi-slot piece; "" for 0.</summary>
    public static string Slot(uint mask)
    {
        if (mask == 0) return string.Empty;
        var sb = new StringBuilder();
        foreach (var (bit, name) in SlotBits)
        {
            if ((mask & bit) == 0) continue;
            if (sb.Length > 0) sb.Append(", ");
            sb.Append(name);
        }
        return sb.Length > 0 ? sb.ToString() : "0x" + mask.ToString("X");
    }

    private static readonly Dictionary<int, string> Materials = new()
    {
        { 1, "Ceramic" }, { 2, "Porcelain" }, { 3, "Cloth" }, { 4, "Linen" }, { 5, "Satin" }, { 6, "Silk" },
        { 7, "Velvet" }, { 8, "Wool" }, { 9, "Gem" }, { 10, "Agate" }, { 11, "Amber" }, { 12, "Amethyst" },
        { 13, "Aquamarine" }, { 14, "Azurite" }, { 15, "Black Garnet" }, { 16, "Black Opal" }, { 17, "Bloodstone" },
        { 18, "Carnelian" }, { 19, "Citrine" }, { 20, "Diamond" }, { 21, "Emerald" }, { 22, "Fire Opal" },
        { 23, "Green Garnet" }, { 24, "Green Jade" }, { 25, "Hematite" }, { 26, "Imperial Topaz" }, { 27, "Jet" },
        { 28, "Lapis Lazuli" }, { 29, "Lavender Jade" }, { 30, "Malachite" }, { 31, "Moonstone" }, { 32, "Onyx" },
        { 33, "Opal" }, { 34, "Peridot" }, { 35, "Red Garnet" }, { 36, "Red Jade" }, { 37, "Rose Quartz" },
        { 38, "Ruby" }, { 39, "Sapphire" }, { 40, "Smoky Quartz" }, { 41, "Sunstone" }, { 42, "Tiger Eye" },
        { 43, "Tourmaline" }, { 44, "Turquoise" }, { 45, "White Jade" }, { 46, "White Quartz" },
        { 47, "White Sapphire" }, { 48, "Yellow Garnet" }, { 49, "Yellow Topaz" }, { 50, "Zircon" }, { 51, "Ivory" },
        { 52, "Leather" }, { 53, "Armoredillo Hide" }, { 54, "Gromnie Hide" }, { 55, "Reedshark Hide" },
        { 56, "Metal" }, { 57, "Brass" }, { 58, "Bronze" }, { 59, "Copper" }, { 60, "Gold" }, { 61, "Iron" },
        { 62, "Pyreal" }, { 63, "Silver" }, { 64, "Steel" }, { 65, "Stone" }, { 66, "Alabaster" }, { 67, "Granite" },
        { 68, "Marble" }, { 69, "Obsidian" }, { 70, "Sandstone" }, { 71, "Serpentine" }, { 72, "Wood" },
        { 73, "Ebony" }, { 74, "Mahogany" }, { 75, "Oak" }, { 76, "Pine" }, { 77, "Teak" },
    };

    public static string Material(int id) => id <= 0 ? string.Empty : Materials.TryGetValue(id, out string? n) ? n : $"Material {id}";

    // Only the sets whose ids are certain (loot armor sets and Aetheria); others show their number.
    private static readonly Dictionary<int, string> Sets = new()
    {
        { 13, "Soldier's" }, { 14, "Adept's" }, { 15, "Archer's" }, { 16, "Defender's" }, { 17, "Tinker's" },
        { 18, "Crafter's" }, { 19, "Hearty" }, { 20, "Dexterous" }, { 21, "Wise" }, { 22, "Swift" },
        { 23, "Hardened" }, { 24, "Reinforced" }, { 25, "Interlocking" }, { 26, "Flame Proof" },
        { 27, "Acid Proof" }, { 28, "Cold Proof" }, { 29, "Lightning Proof" },
        { 35, "Aetheria: Defense" }, { 36, "Aetheria: Destruction" }, { 37, "Aetheria: Fury" },
        { 38, "Aetheria: Growth" }, { 39, "Aetheria: Vigor" },
    };

    public static string Set(int id) => id <= 0 ? string.Empty : Sets.TryGetValue(id, out string? n) ? n + " set" : $"Set {id}";
}

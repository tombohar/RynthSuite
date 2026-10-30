using System;
using System.Collections.Generic;

namespace RynthCore.Plugin.RynthLua;

/// <summary>
/// Enum numbering every API area uses (World classifies objects with ObjectClass, Character
/// reads skills by SkillId, Library exposes the same tables as globals). ObjectClass follows
/// Decal's / UtilityBelt's order, SkillId and AttributeId AC's, so UB scripts' numbers match.
/// </summary>
internal static class LuaEnums
{
    public static readonly string[] ObjectClass =
    {
        "Unknown", "MeleeWeapon", "Armor", "Clothing", "Jewelry", "Monster", "Food", "Money", "Misc",
        "MissileWeapon", "Container", "Gem", "SpellComponent", "Key", "Portal", "TradeNote", "ManaStone",
        "Plant", "BaseCooking", "BaseAlchemy", "BaseFletching", "CraftedCooking", "CraftedAlchemy",
        "CraftedFletching", "Player", "Vendor", "Door", "Corpse", "Lifestone", "HealingKit", "Lockpick",
        "WandStaffOrb", "Bundle", "Book", "Journal", "Sign", "Housing", "Npc", "Foci", "Salvage", "Ust",
        "Services", "Scroll",
    };

    public static readonly Dictionary<string, int> SkillId = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Axe"] = 1, ["Bow"] = 2, ["Crossbow"] = 3, ["Dagger"] = 4, ["Mace"] = 5, ["MeleeDefense"] = 6,
        ["MissileDefense"] = 7, ["Sling"] = 8, ["Spear"] = 9, ["Staff"] = 10, ["Sword"] = 11,
        ["ThrownWeapon"] = 12, ["UnarmedCombat"] = 13, ["ArcaneLore"] = 14, ["MagicDefense"] = 15,
        ["ManaConversion"] = 16, ["Spellcraft"] = 17, ["ItemTinkering"] = 18, ["AssessPerson"] = 19,
        ["Deception"] = 20, ["Healing"] = 21, ["Jump"] = 22, ["Lockpick"] = 23, ["Run"] = 24,
        ["Awareness"] = 25, ["ArmsAndArmorRepair"] = 26, ["AssessCreature"] = 27, ["WeaponTinkering"] = 28,
        ["ArmorTinkering"] = 29, ["MagicItemTinkering"] = 30, ["CreatureEnchantment"] = 31,
        ["ItemEnchantment"] = 32, ["LifeMagic"] = 33, ["WarMagic"] = 34, ["Leadership"] = 35, ["Loyalty"] = 36,
        ["Fletching"] = 37, ["Alchemy"] = 38, ["Cooking"] = 39, ["Salvaging"] = 40, ["TwoHandedCombat"] = 41,
        ["Gearcraft"] = 42, ["VoidMagic"] = 43, ["HeavyWeapons"] = 44, ["LightWeapons"] = 45,
        ["FinesseWeapons"] = 46, ["MissileWeapons"] = 47, ["Shield"] = 48, ["DualWield"] = 49,
        ["Recklessness"] = 50, ["SneakAttack"] = 51, ["DirtyFighting"] = 52, ["Challenge"] = 53, ["Summoning"] = 54,
    };

    public static readonly Dictionary<string, int> AttributeId = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Strength"] = 1, ["Endurance"] = 2, ["Quickness"] = 3, ["Coordination"] = 4, ["Focus"] = 5, ["Self"] = 6,
    };

    /// <summary>
    /// Health 1, Stamina 3, Mana 5: UtilityBelt's VitalId numbering (AC's max-vital ids,
    /// the same numbers InqAttribute2nd uses).
    /// </summary>
    public static readonly Dictionary<string, int> VitalId = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Health"] = 1, ["Stamina"] = 3, ["Mana"] = 5,
    };

    public static readonly Dictionary<string, int> CombatMode = new(StringComparer.OrdinalIgnoreCase)
    {
        ["NonCombat"] = 1, ["Melee"] = 2, ["Missile"] = 4, ["Magic"] = 8,
    };

    public static string CombatModeName(int mode) => mode switch
    {
        1 => "NonCombat", 2 => "Melee", 4 => "Missile", 8 => "Magic", _ => "Undef",
    };

    public static int ObjectClassOf(string name)
    {
        for (int i = 0; i < ObjectClass.Length; i++)
            if (ObjectClass[i].Equals(name, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }
}

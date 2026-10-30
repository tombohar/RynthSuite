using System;

namespace RynthCore.Plugin.RynthDefective.Items;

/// <summary>What an item is, as far as using it goes.</summary>
internal enum ItemKind
{
    /// <summary>Anything used by itself: potions, scrolls, food, gems, summons ...</summary>
    Other,
    /// <summary>A healing or stamina kit: used on yourself.</summary>
    Kit,
    Melee,
    Missile,
    /// <summary>A wand, staff or orb that casts war magic (any element but Nether).</summary>
    WarWand,
    /// <summary>A caster of Nether: void magic.</summary>
    VoidWand,
    Shield,
    Armor,
    Jewelry,
    Ammo,
}

/// <summary>What pressing an item's slot does with it.</summary>
internal enum UseAction
{
    /// <summary>UseObject: double-clicking it (potions, scrolls, food).</summary>
    Use,
    /// <summary>UseObjectOn(item, you): healing and stamina kits.</summary>
    UseOnSelf,
    /// <summary>UseObjectOn(item, the selected target).</summary>
    UseOnSelected,
    /// <summary>Wield or wear it (UseObject: the client's double-click takes what is in the way off).</summary>
    Equip,
}

/// <summary>The weapon picker's groups, in the order they are listed.</summary>
internal enum WeaponGroup { Melee, Missile, WarMagic, VoidMagic, Shield, Ammo }

internal static class ItemKinds
{
    // ItemType flags
    public const uint TypeMelee = 0x1, TypeArmor = 0x2, TypeClothing = 0x4, TypeJewelry = 0x8, TypeMissile = 0x100, TypeCaster = 0x8000;
    // PublicWeenieDesc bitfield: the healer bit
    public const uint BitfieldHealer = 0x10000;
    // EquipMask slots
    public const int ShieldSlot = 0x00200000, AmmoSlot = 0x00800000;
    // CombatUse values
    public const int CombatUseAmmo = 3, CombatUseShield = 4;
    // DamageType / UiEffects bits of Nether
    public const int DamageNether = 0x400, EffectNether = 0x1000;

    public static ItemKind Classify(CarriedItem it)
    {
        bool equippable = it.ValidLocations != 0;
        if (!equippable)
        {
            if ((it.Bitfield & BitfieldHealer) != 0) return ItemKind.Kit;
            // The engine couldn't read the bitfield: RynthAi's own test for the kits it uses.
            if (it.Bitfield == 0 && it.Name.EndsWith(" Kit", StringComparison.OrdinalIgnoreCase)) return ItemKind.Kit;
            return ItemKind.Other;
        }

        bool weaponClass = (it.ItemType & (TypeMelee | TypeMissile | TypeCaster)) != 0 || IsWandName(it.Name);
        if (it.CombatUse == CombatUseAmmo || (it.ValidLocations & AmmoSlot) != 0) return ItemKind.Ammo;
        if (it.CombatUse == CombatUseShield || (!weaponClass && (it.ValidLocations & ShieldSlot) != 0)) return ItemKind.Shield;
        if ((it.ItemType & TypeCaster) != 0 || (weaponClass && (it.ItemType & (TypeMelee | TypeMissile)) == 0))
            return IsNether(it) ? ItemKind.VoidWand : ItemKind.WarWand;
        if ((it.ItemType & TypeMissile) != 0) return ItemKind.Missile;
        if ((it.ItemType & TypeMelee) != 0) return ItemKind.Melee;
        if ((it.ItemType & TypeJewelry) != 0) return ItemKind.Jewelry;
        return ItemKind.Armor;   // armor, clothing and anything else that is worn
    }

    /// <summary>What a slot does with the item when the player has set nothing else.</summary>
    public static UseAction DefaultAction(ItemKind kind) => kind switch
    {
        ItemKind.Kit => UseAction.UseOnSelf,
        ItemKind.Other => UseAction.Use,
        _ => UseAction.Equip,
    };

    public static bool IsWeapon(ItemKind k) => k is ItemKind.Melee or ItemKind.Missile or ItemKind.WarWand or ItemKind.VoidWand;

    /// <summary>The picker group of a kind, or null when the picker doesn't list it.</summary>
    public static WeaponGroup? GroupOf(ItemKind k) => k switch
    {
        ItemKind.Melee => WeaponGroup.Melee,
        ItemKind.Missile => WeaponGroup.Missile,
        ItemKind.WarWand => WeaponGroup.WarMagic,
        ItemKind.VoidWand => WeaponGroup.VoidMagic,
        ItemKind.Shield => WeaponGroup.Shield,
        ItemKind.Ammo => WeaponGroup.Ammo,
        _ => null,
    };

    public static string Label(WeaponGroup g) => g switch
    {
        WeaponGroup.Melee => "Melee",
        WeaponGroup.Missile => "Missile",
        WeaponGroup.WarMagic => "War Magic",
        WeaponGroup.VoidMagic => "Void Magic",
        WeaponGroup.Shield => "Shields",
        _ => "Ammunition",
    };

    /// <summary>A caster whose element is Nether (the damage type, the icon's glow, or the name).</summary>
    public static bool IsNether(CarriedItem it) =>
        (it.DamageType & DamageNether) != 0
        || (it.DamageType == 0 && (it.UiEffects & EffectNether) != 0)
        || (it.DamageType == 0 && it.UiEffects == 0 && HasWord(it.Name, "Nether", "Void"));

    /// <summary>The element a caster is named or glows for, for the picker's second column ("" = none known).</summary>
    public static string Element(CarriedItem it)
    {
        if ((it.DamageType & 0x400) != 0) return "Nether";
        if ((it.DamageType & 0x10) != 0) return "Fire";
        if ((it.DamageType & 0x08) != 0) return "Cold";
        if ((it.DamageType & 0x40) != 0) return "Lightning";
        if ((it.DamageType & 0x20) != 0) return "Acid";
        int fx = it.UiEffects;
        if ((fx & 0x1000) != 0) return "Nether";
        if ((fx & 0x0020) != 0) return "Fire";
        if ((fx & 0x0080) != 0) return "Cold";
        if ((fx & 0x0040) != 0) return "Lightning";
        if ((fx & 0x0100) != 0) return "Acid";
        if ((fx & 0x0400) != 0) return "Slash";
        if ((fx & 0x0800) != 0) return "Pierce";
        if ((fx & 0x0200) != 0) return "Bludgeon";
        return string.Empty;
    }

    public static bool IsWandName(string? name) => HasWord(name, "Orb", "Staff", "Wand", "Scepter", "Sceptre", "Baton", "Crozier");

    private static bool HasWord(string? name, params string[] words)
    {
        if (string.IsNullOrEmpty(name)) return false;
        foreach (string w in words)
            if (name.IndexOf(w, StringComparison.OrdinalIgnoreCase) >= 0) return true;
        return false;
    }

    /// <summary>A launcher needs ammunition in hand; a thrown weapon (a stack of darts) does not.</summary>
    public static bool NeedsAmmo(CarriedItem it) => it.MaxStack <= 1;
}

namespace RynthCore.Loot;

/// <summary>AC item object classes. Matches AC's OBJECT_CLASS enum.</summary>
public enum AcObjectClass
{
    Unknown       = 0,
    MeleeWeapon   = 1,
    Armor         = 2,
    Clothing      = 3,
    MissileWeapon = 4,
    Creature      = 5,
    Ammo          = 6,
    Corpse        = 7,
    Container     = 8,
    WandStaffOrb  = 9,
}

/// <summary>
/// Loot-relevant integer item properties. Values match Chorizite STypeInt sequential enum
/// (UNDEF=0, ITEM_TYPE=1, …). Cast to int when calling item.Values(key, 0).
/// </summary>
public enum AcIntProperty
{
    ArmorLevel        = 28,   // ARMOR_LEVEL
    Burden            = 5,    // ENCUMB_VAL         — encumbrance units
    CombatUse         = 51,   // COMBAT_USE         — melee/missile/magic/shield
    CurrentMana       = 107,  // ITEM_CUR_MANA
    Damage            = 44,   // DAMAGE             — base weapon damage
    DamageType        = 45,   // DAMAGE_TYPE        — slash/pierce/blunt/…
    EquipSlots        = 9,    // LOCATIONS          — equippable slot bits
    ImbuedEffect      = 179,  // IMBUED_EFFECT      — imbue type bitfield
    ItemType          = 1,    // ITEM_TYPE          — item category enum
    LootTier          = 263,  // RESISTANCE_MODIFIER_TYPE — loot tier 1–7
    MaterialType      = 131,  // MATERIAL_TYPE
    MaxMana           = 108,  // ITEM_MAX_MANA
    NumSockets        = 177,  // GEM_COUNT
    NumTimesTinkered  = 171,  // NUM_TIMES_TINKERED
    Spellcraft        = 106,  // ITEM_SPELLCRAFT
    Value             = 19,   // VALUE              — pyreal sell value
    WeaponSkill       = 48,   // WEAPON_SKILL       — skill enum used to wield
    WieldDifficulty   = 160,  // WIELD_DIFFICULTY   — skill level required
    WieldedSlot       = 10,   // CURRENT_WIELDED_LOCATION
    WieldRequirements = 158,  // WIELD_REQUIREMENTS — req type enum
    WieldSkillType    = 159,  // WIELD_SKILLTYPE    — skill type required
    Workmanship       = 105,  // ITEM_WORKMANSHIP
}

/// <summary>
/// Loot-relevant float item properties. Values match Chorizite STypeFloat sequential enum
/// (UNDEF=0, HEARTBEAT_INTERVAL=1, …). Cast to int when calling item.Values(key, 0.0).
/// </summary>
public enum AcFloatProperty
{
    AcidProtMod        = 18,   // ARMOR_MOD_VS_ACID
    AttackBonus        = 61,   // WEAPON_OFFENSE     — attack bonus %
    BluntProtMod       = 15,   // ARMOR_MOD_VS_BLUDGEON
    ColdProtMod        = 16,   // ARMOR_MOD_VS_COLD
    DamageMod          = 62,   // DAMAGE_MOD         — damage bonus %
    DamageVariance     = 22,   // DAMAGE_VARIANCE
    DefenseBonus       = 29,   // WEAPON_DEFENSE     — melee defense bonus %
    ElectricProtMod    = 19,   // ARMOR_MOD_VS_ELECTRIC
    ElementalDamageMod = 152,  // ELEMENTAL_DAMAGE_MOD
    FireProtMod        = 17,   // ARMOR_MOD_VS_FIRE
    MagicDefBonus      = 150,  // WEAPON_MAGIC_DEFENSE
    ManaConvMod        = 143,  // MANA_CONVERSION_MOD
    MissileDefBonus    = 149,  // WEAPON_MISSILE_DEFENSE
    NetherProtMod      = 165,  // ARMOR_MOD_VS_NETHER
    PierceProtMod      = 14,   // ARMOR_MOD_VS_PIERCE
    SlashProtMod       = 13,   // ARMOR_MOD_VS_SLASH
    SlayerDamageBonus  = 137,  // SLAYER_DAMAGE_BONUS
}

/// <summary>AC material types. Integer values match the live acclient.exe property table.</summary>
public enum AcMaterialType
{
    Agate           = 10,
    Alabaster       = 66,
    Amber           = 11,
    Amethyst        = 12,
    Aquamarine      = 13,
    ArmoredilloHide = 53,
    Azurite         = 14,
    BlackGarnet     = 15,
    BlackOpal       = 16,
    Bloodstone      = 17,
    Brass           = 57,
    Bronze          = 58,
    Carnelian       = 18,
    Ceramic         = 1,
    Citrine         = 19,
    Copper          = 59,
    Diamond         = 20,
    Ebony           = 73,
    Emerald         = 21,
    FireOpal        = 22,
    Gold            = 60,
    Granite         = 67,
    GreenGarnet     = 23,
    GreenJade       = 24,
    GromnieHide     = 54,
    Hematite        = 25,
    ImperialTopaz   = 26,
    Iron            = 61,
    Ivory           = 51,
    Jet             = 27,
    LapisLazuli     = 28,
    LavenderJade    = 29,
    Leather         = 52,
    Linen           = 4,
    Mahogany        = 74,
    Malachite       = 30,
    Marble          = 68,
    Moonstone       = 31,
    Oak             = 75,
    Obsidian        = 69,
    Onyx            = 32,
    Opal            = 33,
    Peridot         = 34,
    Pine            = 76,
    Porcelain       = 2,
    Pyreal          = 62,
    RedGarnet       = 35,
    RedJade         = 36,
    ReedSharkHide   = 55,
    RoseQuartz      = 37,
    Ruby            = 38,
    Sandstone       = 70,
    Sapphire        = 39,
    Satin           = 5,
    Serpentine      = 71,
    Silk            = 6,
    Silver          = 63,
    SmokeyQuartz    = 40,
    Steel           = 64,
    Sunstone        = 41,
    Teak            = 77,
    TigerEye        = 42,
    Tourmaline      = 43,
    Turquoise       = 44,
    Velvet          = 7,
    WhiteJade       = 45,
    WhiteQuartz     = 46,
    WhiteSapphire   = 47,
    Wool            = 8,
    YellowGarnet    = 48,
    YellowTopaz     = 49,
    Zircon          = 50,
}

/// <summary>AC skill types. Matches Chorizite STypeSkill sequential enum.</summary>
public enum AcSkillType
{
    Unknown = 0,
    MeleeDefense, MissileDefense, MagicDefense,
    ItemEnchantment, LifeMagic, CreatureEnchantment, WarMagic, VoidMagic,
    HeavyWeapons, LightWeapons, FinesseWeapons, TwoHandedCombat,
    Shield, DualWield, Recklessness, SneakAttack, DirtyFighting,
    ArcaneLore, ArmorTinkering, ItemTinkering, MagicItemTinkering, WeaponTinkering,
    Salvaging, Run, Jump, Loyalty, Leadership, Deception,
    Healing, Lockpick, Cooking, Fletching, Alchemy, ManaConversion,
    AssessCreature, AssessPerson, Summoning,
}

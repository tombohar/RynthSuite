using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using MoonSharp.Interpreter;

namespace RynthCore.Plugin.RynthLua;

/// <summary>
/// <c>require("enums")</c> and the enum globals every script gets, with UtilityBelt's names
/// and numbers: <c>ObjectClass</c>, <c>SkillId</c>, <c>AttributeId</c>, <c>VitalId</c>,
/// <c>CombatMode</c> (numbering from <see cref="LuaEnums"/>, shared with World/Character/
/// Actions), the property ids <c>IntId</c>, <c>Int64Id</c>, <c>FloatId</c>, <c>StringId</c>,
/// <c>BoolId</c>, <c>DataId</c>, <c>InstanceId</c>, <c>ChatType</c> (also as UB's name
/// <c>ChatMessageType</c>), <c>ClientState</c>, and every enum in <see cref="LuaEnumData"/>
/// (ObjectType, EquipMask, MaterialType, ChatChannel, DistanceType, ...).
///
/// Most enums are plain numbers both ways: <c>SkillId.MeleeDefense == 6</c> and
/// <c>SkillId[6] == "MeleeDefense"</c>; names are also found case-insensitively. Numbers get
/// UB's enum helpers through a number metatable: <c>SkillId.Axe:ToNumber()</c>,
/// <c>EquipMask.Head:HasFlags(...)</c>, <c>AddFlags</c>, <c>RemoveFlags</c>.
///
/// The property-id enums other than IntId (<c>Int64Id</c>, <c>FloatId</c>, <c>StringId</c>,
/// <c>BoolId</c>, <c>DataId</c>, <c>InstanceId</c>) and <c>ObjectType</c> are "typed" values
/// instead (<see cref="LuaEnumValues"/>): one table per value, so <c>wo:Value(StringId.Name)</c>
/// can tell a string id from an int id with the same number, and
/// <c>game.World.GetAll(ObjectType.Creature)</c> from an ObjectClass. They print as their name,
/// compare with each other, and have ToNumber / HasFlags / AddFlags / RemoveFlags.
/// <c>ClientState</c> values are the texts <c>game.State</c> returns
/// (<c>ClientState.In_Game == "InGame"</c>), like <c>ActionError</c>.
/// </summary>
internal static class LibraryEnums
{
    /// <summary>Enums whose values are typed tables (see the class summary).</summary>
    public static readonly HashSet<string> TypedEnums = new(StringComparer.Ordinal)
    {
        "Int64Id", "FloatId", "StringId", "BoolId", "DataId", "InstanceId", "ObjectType",
    };

    /// <summary>UB's names that differ from RynthLua's first name for the same number (kept as aliases).</summary>
    private static readonly (string Enum, string Name, int Value)[] Aliases =
    {
        ("SkillId", "Undef", 0), ("SkillId", "ThrownWeapons", 12), ("SkillId", "ArmorRepair", 26),
        ("SkillId", "MissleWeapons", 47), ("AttributeId", "Undef", 0), ("CombatMode", "Invalid", 153),
        ("ObjectClass", "NumObjectClasses", 43), ("IntId", "MerchandiseObjectTypes", 74),
        ("IntId", "HookObjectType", 152),
    };

    /// <summary>UB's ClientState names → the texts game.State returns (the others never occur).</summary>
    private static readonly (string Name, string Value)[] ClientStates =
    {
        ("Initial", "Initial"), ("Game_Started", "Game_Started"), ("Character_Select_Screen", "CharacterSelect"),
        ("Creating_Character", "Creating_Character"), ("Entering_Game", "Entering_Game"),
        ("PlayerDesc_Received", "PlayerDesc_Received"), ("In_Game", "InGame"), ("Logging_Out", "LoggingOut"),
        ("Disconnected", "Disconnected"),
    };

    public static DynValue CreateModule(Script s)
    {
        LuaEnumValues.InstallNumberHelpers(s);
        var m = new Table(s);

        void Add(string name, IEnumerable<(string, long)> members)
        {
            var list = new List<(string, long)>(members);
            foreach (var (e, n, v) in Aliases)
                if (e == name) list.Add((n, v));
            LuaEnumValues.Define(name, list, LuaEnumValues.LooksLikeFlags(list));
            m.Set(name, TypedEnums.Contains(name) ? BuildTyped(s, name, list) : Build(s, name, list));
        }

        var objectClass = new List<(string, long)>(LuaEnums.ObjectClass.Length);
        for (int i = 0; i < LuaEnums.ObjectClass.Length; i++) objectClass.Add((LuaEnums.ObjectClass[i], i));

        Add("ObjectClass", objectClass);
        Add("SkillId", Pairs(LuaEnums.SkillId));
        Add("AttributeId", Pairs(LuaEnums.AttributeId));
        Add("VitalId", Pairs(LuaEnums.VitalId));
        Add("CombatMode", Pairs(LuaEnums.CombatMode));
        Add("IntId", Widen(IntIds));
        Add("BoolId", Widen(BoolIds));
        Add("StringId", Widen(StringIds));
        Add("ChatType", Widen(ChatTypes));
        foreach (var (name, _, members) in LuaEnumData.All) Add(name, members);
        m.Set("ChatMessageType", m.Get("ChatType"));   // UB's name, the same table

        var states = new Table(s);
        foreach (var (n, v) in ClientStates) states.Set(n, DynValue.NewString(v));
        var stMeta = new Table(s);
        stMeta.Set("__tostring", DynValue.NewCallback((c, a) => DynValue.NewString("enum ClientState")));
        states.MetaTable = stMeta;
        m.Set("ClientState", DynValue.NewTable(states));

        var mt = new Table(s);
        mt.Set("__tostring", DynValue.NewCallback((c, a) => DynValue.NewString("module enums")));
        m.MetaTable = mt;
        return DynValue.NewTable(m);
    }

    private static IEnumerable<(string, long)> Pairs(Dictionary<string, int> d)
    {
        var list = new List<(string, long)>(d.Count);
        foreach (var kv in d) list.Add((kv.Key, kv.Value));
        list.Sort((x, y) => x.Item2.CompareTo(y.Item2));
        return list;
    }

    private static IEnumerable<(string, long)> Widen(IEnumerable<(string, int)> members)
    {
        foreach (var (n, v) in members) yield return (n, v);
    }

    /// <summary>Int-valued members (LibraryImGuiValues' ImGui enums): see the long overload.</summary>
    internal static DynValue Build(Script s, string enumName, IEnumerable<(string Name, int Value)> members)
        => Build(s, enumName, Widen(members));

    /// <summary>A table with name → number and number → name (the first name for a number wins); names are also found case-insensitively. Shared with LibraryImGuiValues.</summary>
    internal static DynValue Build(Script s, string enumName, IEnumerable<(string Name, long Value)> members)
    {
        var t = new Table(s);
        var byName = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in members)
        {
            // Number keys as DynValues: MoonSharp's Table.Set(int 0, ...) lands where Lua's
            // t[0] never looks (ObjectClass.Unknown is 0).
            DynValue number = DynValue.NewNumber(value);
            t.Set(name, number);
            if (t.RawGet(number) == null) t.Set(number, DynValue.NewString(name));
            byName.TryAdd(name, value);
        }

        var mt = new Table(s);
        // Only consulted for keys the table doesn't have: a differently-cased name.
        mt.Set("__index", DynValue.NewCallback((c, a) =>
        {
            DynValue key = a.Count > 1 ? a[1] : DynValue.Nil;
            return key.Type == DataType.String && byName.TryGetValue(key.String, out long v) ? DynValue.NewNumber(v) : DynValue.Nil;
        }));
        mt.Set("__tostring", DynValue.NewCallback((c, a) => DynValue.NewString("enum " + enumName)));
        t.MetaTable = mt;
        return DynValue.NewTable(t);
    }

    /// <summary>Like <see cref="Build"/>, but name → typed value (number → name stays a name).</summary>
    private static DynValue BuildTyped(Script s, string enumName, IEnumerable<(string Name, long Value)> members)
    {
        var t = new Table(s);
        var byName = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in members)
        {
            t.Set(name, LuaEnumValues.Get(s, enumName, value));
            DynValue number = DynValue.NewNumber(value);
            if (t.RawGet(number) == null) t.Set(number, DynValue.NewString(name));
            byName.TryAdd(name, value);
        }
        var mt = new Table(s);
        mt.Set("__index", DynValue.NewCallback((c, a) =>
        {
            DynValue key = a.Count > 1 ? a[1] : DynValue.Nil;
            return key.Type == DataType.String && byName.TryGetValue(key.String, out long v) ? LuaEnumValues.Get(s, enumName, v) : DynValue.Nil;
        }));
        mt.Set("__tostring", DynValue.NewCallback((c, a) => DynValue.NewString("enum " + enumName)));
        t.MetaTable = mt;
        return DynValue.NewTable(t);
    }

    /// <summary>AC's chat message types (UtilityBelt's ChatMessageType names and values).</summary>
    private static readonly (string, int)[] ChatTypes =
    {
        ("Default", 0), ("Speech", 2), ("Tell", 3), ("OutgoingTell", 4), ("System", 5), ("Combat", 6),
        ("Magic", 7), ("Channels", 8), ("OutgoingChannel", 9), ("Social", 10), ("OutgoingSocial", 11),
        ("Emote", 12), ("Advancement", 13), ("Abuse", 14), ("Help", 15), ("Appraisal", 16),
        ("Spellcasting", 17), ("Allegiance", 18), ("Fellowship", 19), ("WorldBroadcast", 20),
        ("CombatEnemy", 21), ("CombatSelf", 22), ("Recall", 23), ("Craft", 24), ("Salvaging", 25),
        ("AdminTell", 31),
    };

    // Property ids, from ACE's PropertyInt / PropertyBool / PropertyString (the same table
    // RynthAi's Meta/PropertyNames.cs uses), plus UB's alias IntId.ObjectType = 1.

    private static readonly (string, int)[] IntIds =
    {
        ("Undef", 0), ("ItemType", 1), ("CreatureType", 2), ("PaletteTemplate", 3), ("ClothingPriority", 4),
        ("EncumbranceVal", 5), ("ItemsCapacity", 6), ("ContainersCapacity", 7), ("Mass", 8), ("ValidLocations", 9),
        ("CurrentWieldedLocation", 10), ("MaxStackSize", 11), ("StackSize", 12), ("StackUnitEncumbrance", 13),
        ("StackUnitMass", 14), ("StackUnitValue", 15), ("ItemUseable", 16), ("RareId", 17), ("UiEffects", 18),
        ("Value", 19), ("CoinValue", 20), ("TotalExperience", 21), ("AvailableCharacter", 22),
        ("TotalSkillCredits", 23), ("AvailableSkillCredits", 24), ("Level", 25), ("AccountRequirements", 26),
        ("ArmorType", 27), ("ArmorLevel", 28), ("AllegianceCpPool", 29), ("AllegianceRank", 30),
        ("ChannelsAllowed", 31), ("ChannelsActive", 32), ("Bonded", 33), ("MonarchsRank", 34),
        ("AllegianceFollowers", 35), ("ResistMagic", 36), ("ResistItemAppraisal", 37), ("ResistLockpick", 38),
        ("DeprecatedResistRepair", 39), ("CombatMode", 40), ("CurrentAttackHeight", 41), ("CombatCollisions", 42),
        ("NumDeaths", 43), ("Damage", 44), ("DamageType", 45), ("DefaultCombatStyle", 46), ("AttackType", 47),
        ("WeaponSkill", 48), ("WeaponTime", 49), ("AmmoType", 50), ("CombatUse", 51), ("ParentLocation", 52),
        ("PlacementPosition", 53), ("WeaponEncumbrance", 54), ("WeaponMass", 55), ("ShieldValue", 56),
        ("ShieldEncumbrance", 57), ("MissileInventoryLocation", 58), ("FullDamageType", 59), ("WeaponRange", 60),
        ("AttackersSkill", 61), ("DefendersSkill", 62), ("AttackersSkillValue", 63), ("AttackersClass", 64),
        ("Placement", 65), ("CheckpointStatus", 66), ("Tolerance", 67), ("TargetingTactic", 68),
        ("CombatTactic", 69), ("HomesickTargetingTactic", 70), ("NumFollowFailures", 71), ("FriendType", 72),
        ("FoeType", 73), ("MerchandiseItemTypes", 74), ("MerchandiseMinValue", 75), ("MerchandiseMaxValue", 76),
        ("NumItemsSold", 77), ("NumItemsBought", 78), ("MoneyIncome", 79), ("MoneyOutflow", 80),
        ("MaxGeneratedObjects", 81), ("InitGeneratedObjects", 82), ("ActivationResponse", 83), ("OriginalValue", 84),
        ("NumMoveFailures", 85), ("MinLevel", 86), ("MaxLevel", 87), ("LockpickMod", 88), ("BoosterEnum", 89),
        ("BoostValue", 90), ("MaxStructure", 91), ("Structure", 92), ("PhysicsState", 93), ("TargetType", 94),
        ("RadarBlipColor", 95), ("EncumbranceCapacity", 96), ("LoginTimestamp", 97), ("CreationTimestamp", 98),
        ("PkLevelModifier", 99), ("GeneratorType", 100), ("AiAllowedCombatStyle", 101), ("LogoffTimestamp", 102),
        ("GeneratorDestructionType", 103), ("ActivationCreateClass", 104), ("ItemWorkmanship", 105),
        ("ItemSpellcraft", 106), ("ItemCurMana", 107), ("ItemMaxMana", 108), ("ItemDifficulty", 109),
        ("ItemAllegianceRankLimit", 110), ("PortalBitmask", 111), ("AdvocateLevel", 112), ("Gender", 113),
        ("Attuned", 114), ("ItemSkillLevelLimit", 115), ("GateLogic", 116), ("ItemManaCost", 117), ("Logoff", 118),
        ("Active", 119), ("AttackHeight", 120), ("NumAttackFailures", 121), ("AiCpThreshold", 122),
        ("AiAdvancementStrategy", 123), ("Version", 124), ("Age", 125), ("VendorHappyMean", 126),
        ("VendorHappyVariance", 127), ("CloakStatus", 128), ("VitaeCpPool", 129), ("NumServicesSold", 130),
        ("MaterialType", 131), ("NumAllegianceBreaks", 132), ("ShowableOnRadar", 133), ("PlayerKillerStatus", 134),
        ("VendorHappyMaxItems", 135), ("ScorePageNum", 136), ("ScoreConfigNum", 137), ("ScoreNumScores", 138),
        ("DeathLevel", 139), ("AiOptions", 140), ("OpenToEveryone", 141), ("GeneratorTimeType", 142),
        ("GeneratorStartTime", 143), ("GeneratorEndTime", 144), ("GeneratorEndDestructionType", 145),
        ("XpOverride", 146), ("NumCrashAndTurns", 147), ("ComponentWarningThreshold", 148), ("HouseStatus", 149),
        ("HookPlacement", 150), ("HookType", 151), ("HookItemType", 152), ("AiPpThreshold", 153),
        ("GeneratorVersion", 154), ("HouseType", 155), ("PickupEmoteOffset", 156), ("WeenieIteration", 157),
        ("WieldRequirements", 158), ("WieldSkillType", 159), ("WieldDifficulty", 160), ("HouseMaxHooksUsable", 161),
        ("HouseCurrentHooksUsable", 162), ("AllegianceMinLevel", 163), ("AllegianceMaxLevel", 164),
        ("HouseRelinkHookCount", 165), ("SlayerCreatureType", 166), ("ConfirmationInProgress", 167),
        ("ConfirmationTypeInProgress", 168), ("TsysMutationData", 169), ("NumItemsInMaterial", 170),
        ("NumTimesTinkered", 171), ("AppraisalLongDescDecoration", 172), ("AppraisalLockpickSuccessPercent", 173),
        ("AppraisalPages", 174), ("AppraisalMaxPages", 175), ("AppraisalItemSkill", 176), ("GemCount", 177),
        ("GemType", 178), ("ImbuedEffect", 179), ("AttackersRawSkillValue", 180), ("ChessRank", 181),
        ("ChessTotalGames", 182), ("ChessGamesWon", 183), ("ChessGamesLost", 184), ("TypeOfAlteration", 185),
        ("SkillToBeAltered", 186), ("SkillAlterationCount", 187), ("HeritageGroup", 188),
        ("TransferFromAttribute", 189), ("TransferToAttribute", 190), ("AttributeTransferCount", 191),
        ("FakeFishingSkill", 192), ("NumKeys", 193), ("DeathTimestamp", 194), ("PkTimestamp", 195),
        ("VictimTimestamp", 196), ("HookGroup", 197), ("AllegianceSwearTimestamp", 198),
        ("HousePurchaseTimestamp", 199), ("RedirectableEquippedArmorCount", 200),
        ("MeleeDefenseImbuedEffectTypeCache", 201), ("MissileDefenseImbuedEffectTypeCache", 202),
        ("MagicDefenseImbuedEffectTypeCache", 203), ("ElementalDamageBonus", 204), ("ImbueAttempts", 205),
        ("ImbueSuccesses", 206), ("CreatureKills", 207), ("PlayerKillsPk", 208), ("PlayerKillsPkl", 209),
        ("RaresTierOne", 210), ("RaresTierTwo", 211), ("RaresTierThree", 212), ("RaresTierFour", 213),
        ("RaresTierFive", 214), ("AugmentationStat", 215), ("AugmentationFamilyStat", 216),
        ("AugmentationInnateFamily", 217), ("AugmentationInnateStrength", 218), ("AugmentationInnateEndurance", 219),
        ("AugmentationInnateCoordination", 220), ("AugmentationInnateQuickness", 221),
        ("AugmentationInnateFocus", 222), ("AugmentationInnateSelf", 223), ("AugmentationSpecializeSalvaging", 224),
        ("AugmentationSpecializeItemTinkering", 225), ("AugmentationSpecializeArmorTinkering", 226),
        ("AugmentationSpecializeMagicItemTinkering", 227), ("AugmentationSpecializeWeaponTinkering", 228),
        ("AugmentationExtraPackSlot", 229), ("AugmentationIncreasedCarryingCapacity", 230),
        ("AugmentationLessDeathItemLoss", 231), ("AugmentationSpellsRemainPastDeath", 232),
        ("AugmentationCriticalDefense", 233), ("AugmentationBonusXp", 234), ("AugmentationBonusSalvage", 235),
        ("AugmentationBonusImbueChance", 236), ("AugmentationFasterRegen", 237),
        ("AugmentationIncreasedSpellDuration", 238), ("AugmentationResistanceFamily", 239),
        ("AugmentationResistanceSlash", 240), ("AugmentationResistancePierce", 241),
        ("AugmentationResistanceBlunt", 242), ("AugmentationResistanceAcid", 243),
        ("AugmentationResistanceFire", 244), ("AugmentationResistanceFrost", 245),
        ("AugmentationResistanceLightning", 246), ("RaresTierOneLogin", 247), ("RaresTierTwoLogin", 248),
        ("RaresTierThreeLogin", 249), ("RaresTierFourLogin", 250), ("RaresTierFiveLogin", 251),
        ("RaresLoginTimestamp", 252), ("RaresTierSix", 253), ("RaresTierSeven", 254), ("RaresTierSixLogin", 255),
        ("RaresTierSevenLogin", 256), ("ItemAttributeLimit", 257), ("ItemAttributeLevelLimit", 258),
        ("ItemAttribute2ndLimit", 259), ("ItemAttribute2ndLevelLimit", 260), ("CharacterTitleId", 261),
        ("NumCharacterTitles", 262), ("ResistanceModifierType", 263), ("FreeTinkersBitfield", 264),
        ("EquipmentSetId", 265), ("PetClass", 266), ("Lifespan", 267), ("RemainingLifespan", 268),
        ("UseCreateQuantity", 269), ("WieldRequirements2", 270), ("WieldSkillType2", 271), ("WieldDifficulty2", 272),
        ("WieldRequirements3", 273), ("WieldSkillType3", 274), ("WieldDifficulty3", 275),
        ("WieldRequirements4", 276), ("WieldSkillType4", 277), ("WieldDifficulty4", 278), ("Unique", 279),
        ("SharedCooldown", 280), ("Faction1Bits", 281), ("Faction2Bits", 282), ("Faction3Bits", 283),
        ("Hatred1Bits", 284), ("Hatred2Bits", 285), ("Hatred3Bits", 286), ("SocietyRankCelhan", 287),
        ("SocietyRankEldweb", 288), ("SocietyRankRadblo", 289), ("HearLocalSignals", 290),
        ("HearLocalSignalsRadius", 291), ("Cleaving", 292), ("AugmentationSpecializeGearcraft", 293),
        ("AugmentationInfusedCreatureMagic", 294), ("AugmentationInfusedItemMagic", 295),
        ("AugmentationInfusedLifeMagic", 296), ("AugmentationInfusedWarMagic", 297),
        ("AugmentationCriticalExpertise", 298), ("AugmentationCriticalPower", 299),
        ("AugmentationSkilledMelee", 300), ("AugmentationSkilledMissile", 301), ("AugmentationSkilledMagic", 302),
        ("ImbuedEffect2", 303), ("ImbuedEffect3", 304), ("ImbuedEffect4", 305), ("ImbuedEffect5", 306),
        ("DamageRating", 307), ("DamageResistRating", 308), ("AugmentationDamageBonus", 309),
        ("AugmentationDamageReduction", 310), ("ImbueStackingBits", 311), ("HealOverTime", 312), ("CritRating", 313),
        ("CritDamageRating", 314), ("CritResistRating", 315), ("CritDamageResistRating", 316),
        ("HealingResistRating", 317), ("DamageOverTime", 318), ("ItemMaxLevel", 319), ("ItemXpStyle", 320),
        ("EquipmentSetExtra", 321), ("AetheriaBitfield", 322), ("HealingBoostRating", 323),
        ("HeritageSpecificArmor", 324), ("AlternateRacialSkills", 325), ("AugmentationJackOfAllTrades", 326),
        ("AugmentationResistanceNether", 327), ("AugmentationInfusedVoidMagic", 328), ("WeaknessRating", 329),
        ("NetherOverTime", 330), ("NetherResistRating", 331), ("LuminanceAward", 332), ("LumAugDamageRating", 333),
        ("LumAugDamageReductionRating", 334), ("LumAugCritDamageRating", 335), ("LumAugCritReductionRating", 336),
        ("LumAugSurgeEffectRating", 337), ("LumAugSurgeChanceRating", 338), ("LumAugItemManaUsage", 339),
        ("LumAugItemManaGain", 340), ("LumAugVitality", 341), ("LumAugHealingRating", 342),
        ("LumAugSkilledCraft", 343), ("LumAugSkilledSpec", 344), ("LumAugNoDestroyCraft", 345),
        ("RestrictInteraction", 346), ("OlthoiLootTimestamp", 347), ("OlthoiLootStep", 348),
        ("UseCreatesContractId", 349), ("DotResistRating", 350), ("LifeResistRating", 351), ("CloakWeaveProc", 352),
        ("WeaponType", 353), ("MeleeMastery", 354), ("RangedMastery", 355), ("SneakAttackRating", 356),
        ("RecklessnessRating", 357), ("DeceptionRating", 358), ("CombatPetRange", 359), ("WeaponAuraDamage", 360),
        ("WeaponAuraSpeed", 361), ("SummoningMastery", 362), ("HeartbeatLifespan", 363),
        ("UseLevelRequirement", 364), ("LumAugAllSkills", 365), ("UseRequiresSkill", 366),
        ("UseRequiresSkillLevel", 367), ("UseRequiresSkillSpec", 368), ("UseRequiresLevel", 369),
        ("GearDamage", 370), ("GearDamageResist", 371), ("GearCrit", 372), ("GearCritResist", 373),
        ("GearCritDamage", 374), ("GearCritDamageResist", 375), ("GearHealingBoost", 376), ("GearNetherResist", 377),
        ("GearLifeResist", 378), ("GearMaxHealth", 379), ("Unknown380", 380), ("PKDamageRating", 381),
        ("PKDamageResistRating", 382), ("GearPKDamageRating", 383), ("GearPKDamageResistRating", 384),
        ("Unknown385", 385), ("Overpower", 386), ("OverpowerResist", 387), ("GearOverpower", 388),
        ("GearOverpowerResist", 389), ("Enlightenment", 390), ("PCAPRecordedAutonomousMovement", 8007),
        ("PCAPRecordedMaxVelocityEstimated", 8030), ("PCAPRecordedPlacement", 8041),
        ("PCAPRecordedAppraisalPages", 8042), ("PCAPRecordedAppraisalMaxPages", 8043), ("TotalLogins", 9001),
        ("DeletionTimestamp", 9002), ("CharacterOptions1", 9003), ("CharacterOptions2", 9004), ("LootTier", 9005),
        ("GeneratorProbability", 9006), ("CurrentLoyaltyAtLastLogoff", 9008),
        ("CurrentLeadershipAtLastLogoff", 9009), ("AllegianceOfficerRank", 9010), ("HouseRentTimestamp", 9011),
        ("Hairstyle", 9012), ("VisualClothingPriority", 9013), ("SquelchGlobal", 9014), ("InventoryOrder", 9015),
        ("ObjectType", 1),
    };

    private static readonly (string, int)[] BoolIds =
    {
        ("Undef", 0), ("Stuck", 1), ("Open", 2), ("Locked", 3), ("RotProof", 4), ("AllegianceUpdateRequest", 5),
        ("AiUsesMana", 6), ("AiUseHumanMagicAnimations", 7), ("AllowGive", 8), ("CurrentlyAttacking", 9),
        ("AttackerAi", 10), ("IgnoreCollisions", 11), ("ReportCollisions", 12), ("Ethereal", 13),
        ("GravityStatus", 14), ("LightsStatus", 15), ("ScriptedCollision", 16), ("Inelastic", 17),
        ("Visibility", 18), ("Attackable", 19), ("SafeSpellComponents", 20), ("AdvocateState", 21),
        ("Inscribable", 22), ("DestroyOnSell", 23), ("UiHidden", 24), ("IgnoreHouseBarriers", 25),
        ("HiddenAdmin", 26), ("PkWounder", 27), ("PkKiller", 28), ("NoCorpse", 29), ("UnderLifestoneProtection", 30),
        ("ItemManaUpdatePending", 31), ("GeneratorStatus", 32), ("ResetMessagePending", 33), ("DefaultOpen", 34),
        ("DefaultLocked", 35), ("DefaultOn", 36), ("OpenForBusiness", 37), ("IsFrozen", 38),
        ("DealMagicalItems", 39), ("LogoffImDead", 40), ("ReportCollisionsAsEnvironment", 41),
        ("AllowEdgeSlide", 42), ("AdvocateQuest", 43), ("IsAdmin", 44), ("IsArch", 45), ("IsSentinel", 46),
        ("IsAdvocate", 47), ("CurrentlyPoweringUp", 48), ("GeneratorEnteredWorld", 49), ("NeverFailCasting", 50),
        ("VendorService", 51), ("AiImmobile", 52), ("DamagedByCollisions", 53), ("IsDynamic", 54), ("IsHot", 55),
        ("IsAffecting", 56), ("AffectsAis", 57), ("SpellQueueActive", 58), ("GeneratorDisabled", 59),
        ("IsAcceptingTells", 60), ("LoggingChannel", 61), ("OpensAnyLock", 62), ("UnlimitedUse", 63),
        ("GeneratedTreasureItem", 64), ("IgnoreMagicResist", 65), ("IgnoreMagicArmor", 66), ("AiAllowTrade", 67),
        ("SpellComponentsRequired", 68), ("IsSellable", 69), ("IgnoreShieldsBySkill", 70), ("NoDraw", 71),
        ("ActivationUntargeted", 72), ("HouseHasGottenPriorityBootPos", 73), ("GeneratorAutomaticDestruction", 74),
        ("HouseHooksVisible", 75), ("HouseRequiresMonarch", 76), ("HouseHooksEnabled", 77),
        ("HouseNotifiedHudOfHookCount", 78), ("AiAcceptEverything", 79), ("IgnorePortalRestrictions", 80),
        ("RequiresBackpackSlot", 81), ("DontTurnOrMoveWhenGiving", 82), ("NpcLooksLikeObject", 83),
        ("IgnoreCloIcons", 84), ("AppraisalHasAllowedWielder", 85), ("ChestRegenOnClose", 86),
        ("LogoffInMinigame", 87), ("PortalShowDestination", 88), ("PortalIgnoresPkAttackTimer", 89),
        ("NpcInteractsSilently", 90), ("Retained", 91), ("IgnoreAuthor", 92), ("Limbo", 93),
        ("AppraisalHasAllowedActivator", 94), ("ExistedBeforeAllegianceXpChanges", 95), ("IsDeaf", 96),
        ("IsPsr", 97), ("Invincible", 98), ("Ivoryable", 99), ("Dyable", 100), ("CanGenerateRare", 101),
        ("CorpseGeneratedRare", 102), ("NonProjectileMagicImmune", 103), ("ActdReceivedItems", 104),
        ("Unknown105", 105), ("FirstEnterWorldDone", 106), ("RecallsDisabled", 107), ("RareUsesTimer", 108),
        ("ActdPreorderReceivedItems", 109), ("Afk", 110), ("IsGagged", 111), ("ProcSpellSelfTargeted", 112),
        ("IsAllegianceGagged", 113), ("EquipmentSetTriggerPiece", 114), ("Uninscribe", 115), ("WieldOnUse", 116),
        ("ChestClearedWhenClosed", 117), ("NeverAttack", 118), ("SuppressGenerateEffect", 119),
        ("TreasureCorpse", 120), ("EquipmentSetAddLevel", 121), ("BarberActive", 122), ("TopLayerPriority", 123),
        ("NoHeldItemShown", 124), ("LoginAtLifestone", 125), ("OlthoiPk", 126), ("Account15Days", 127),
        ("HadNoVitae", 128), ("NoOlthoiTalk", 129), ("AutowieldLeft", 130), ("LinkedPortalOneSummon", 9001),
        ("LinkedPortalTwoSummon", 9002), ("HouseEvicted", 9003), ("UntrainedSkills", 9004), ("IsEnvoy", 9005),
        ("UnspecializedSkills", 9006), ("FreeSkillResetRenewed", 9007), ("FreeAttributeResetRenewed", 9008),
        ("SkillTemplesTimerReset", 9009), ("FreeMasteryResetRenewed", 9010),
    };

    private static readonly (string, int)[] StringIds =
    {
        ("Undef", 0), ("Name", 1), ("Title", 2), ("Sex", 3), ("HeritageGroup", 4), ("Template", 5),
        ("AttackersName", 6), ("Inscription", 7), ("ScribeName", 8), ("VendorsName", 9), ("Fellowship", 10),
        ("MonarchsName", 11), ("LockCode", 12), ("KeyCode", 13), ("Use", 14), ("ShortDesc", 15), ("LongDesc", 16),
        ("ActivationTalk", 17), ("UseMessage", 18), ("ItemHeritageGroupRestriction", 19), ("PluralName", 20),
        ("MonarchsTitle", 21), ("ActivationFailure", 22), ("ScribeAccount", 23), ("TownName", 24),
        ("CraftsmanName", 25), ("UsePkServerError", 26), ("ScoreCachedText", 27), ("ScoreDefaultEntryFormat", 28),
        ("ScoreFirstEntryFormat", 29), ("ScoreLastEntryFormat", 30), ("ScoreOnlyEntryFormat", 31),
        ("ScoreNoEntry", 32), ("Quest", 33), ("GeneratorEvent", 34), ("PatronsTitle", 35), ("HouseOwnerName", 36),
        ("QuestRestriction", 37), ("AppraisalPortalDestination", 38), ("TinkerName", 39), ("ImbuerName", 40),
        ("HouseOwnerAccount", 41), ("DisplayName", 42), ("DateOfBirth", 43), ("ThirdPartyApi", 44),
        ("KillQuest", 45), ("Afk", 46), ("AllegianceName", 47), ("AugmentationAddQuest", 48), ("KillQuest2", 49),
        ("KillQuest3", 50), ("UseSendsSignal", 51), ("GearPlatingName", 52),
        ("PCAPRecordedCurrentMotionState", 8006), ("PCAPRecordedServerName", 8031),
        ("PCAPRecordedCharacterName", 8032), ("AllegianceMotd", 9001), ("AllegianceMotdSetBy", 9002),
        ("AllegianceSpeakerTitle", 9003), ("AllegianceSeneschalTitle", 9004), ("AllegianceCastellanTitle", 9005),
        ("GodState", 9006), ("TinkerLog", 9007),
    };
}

namespace RynthCore.Plugin.RynthAi.Meta;

/// <summary>
/// AC property enum names from STypes.cs — sequential from 0, no gaps.
/// Used by dumpprops to label property IDs.
/// </summary>
internal static class PropertyNames
{
    private static readonly string[] IntNames =
    {
        "Undef", "ItemType", "CreatureType", "PaletteTemplate", "ClothingPriority",
        "EncumbVal", "ItemsCapacity", "ContainersCapacity", "Mass", "Locations",
        "CurrentWieldedLocation", "MaxStackSize", "StackSize", "StackUnitEncumb", "StackUnitMass",
        "StackUnitValue", "ItemUseable", "RareId", "UiEffects", "Value",
        "CoinValue", "TotalExperience", "AvailableCharacter", "TotalSkillCredits", "AvailableSkillCredits",
        "Level", "AccountRequirements", "ArmorType", "ArmorLevel", "AllegianceCpPool",
        "AllegianceRank", "ChannelsAllowed", "ChannelsActive", "Bonded", "MonarchsRank",
        "AllegianceFollowers", "ResistMagic", "ResistItemAppraisal", "ResistLockpick", "DeprecatedResistRepair",
        "CombatMode", "CurrentAttackHeight", "CombatCollisions", "NumDeaths", "Damage",
        "DamageType", "DefaultCombatStyle", "AttackType", "WeaponSkill", "WeaponTime",
        "AmmoType", "CombatUse", "ParentLocation", "PlacementPosition", "WeaponEncumbrance",
        "WeaponMass", "ShieldValue", "ShieldEncumbrance", "MissileInventoryLocation", "FullDamageType",
        "WeaponRange", "AttackersSkill", "DefendersSkill", "AttackersSkillValue", "AttackersClass",
        "Placement", "CheckpointStatus", "Tolerance", "TargetingTactic", "CombatTactic",
        "HomesickTargetingTactic", "NumFollowFailures", "FriendType", "FoeType", "MerchandiseItemTypes",
        "MerchandiseMinValue", "MerchandiseMaxValue", "NumItemsSold", "NumItemsBought", "MoneyIncome",
        "MoneyOutflow", "MaxGeneratedObjects", "InitGeneratedObjects", "ActivationResponse", "OriginalValue",
        "NumMoveFailures", "MinLevel", "MaxLevel", "LockpickMod", "BoosterEnum",
        "BoostValue", "MaxStructure", "Structure", "PhysicsState", "TargetType",
        "RadarblipColor", "EncumbCapacity", "LoginTimestamp", "CreationTimestamp", "PkLevelModifier",
        "GeneratorType", "AiAllowedCombatStyle", "LogoffTimestamp", "GeneratorDestructionType", "ActivationCreateClass",
        "ItemWorkmanship", "ItemSpellcraft", "ItemCurMana", "ItemMaxMana", "ItemDifficulty",
        "ItemAllegianceRankLimit", "PortalBitmask", "AdvocateLevel", "Gender", "Attuned",
        "ItemSkillLevelLimit", "GateLogic", "ItemManaCost", "Logoff", "Active",
        "AttackHeight", "NumAttackFailures", "AiCpThreshold", "AiAdvancementStrategy", "Version",
        "Age", "VendorHappyMean", "VendorHappyVariance", "CloakStatus", "VitaeCpPool",
        "NumServicesSold", "MaterialType", "NumAllegianceBreaks", "ShowableOnRadar", "PlayerKillerStatus",
        "VendorHappyMaxItems", "ScorePageNum", "ScoreConfigNum", "ScoreNumScores", "DeathLevel",
        "AiOptions", "OpenToEveryone", "GeneratorTimeType", "GeneratorStartTime", "GeneratorEndTime",
        "GeneratorEndDestructionType", "XpOverride", "NumCrashAndTurns", "ComponentWarningThreshold", "HouseStatus",
        "HookPlacement", "HookType", "HookItemType", "AiPpThreshold", "GeneratorVersion",
        "HouseType", "PickupEmoteOffset", "WeenieIteration", "WieldRequirements", "WieldSkilltype",
        "WieldDifficulty", "HouseMaxHooksUsable", "HouseCurrentHooksUsable", "AllegianceMinLevel", "AllegianceMaxLevel",
        "HouseRelinkHookCount", "SlayerCreatureType", "ConfirmationInProgress", "ConfirmationTypeInProgress", "TsysMutationData",
        "NumItemsInMaterial", "NumTimesTinkered", "AppraisalLongDescDecoration", "AppraisalLockpickSuccessPercent", "AppraisalPages",
        "AppraisalMaxPages", "AppraisalItemSkill", "GemCount", "GemType", "ImbuedEffect",
        "AttackersRawSkillValue", "ChessRank", "ChessTotalGames", "ChessGamesWon", "ChessGamesLost",
        "TypeOfAlteration", "SkillToBeAltered", "SkillAlterationCount", "HeritageGroup", "TransferFromAttribute",
        "TransferToAttribute", "AttributeTransferCount", "FakeFishingSkill", "NumKeys", "DeathTimestamp",
        "PkTimestamp", "VictimTimestamp", "HookGroup", "AllegianceSwearTimestamp", "HousePurchaseTimestamp",
        "RedirectableEquippedArmorCount", "MeleeDefenseImbuedEffectTypeCache", "MissileDefenseImbuedEffectTypeCache", "MagicDefenseImbuedEffectTypeCache", "ElementalDamageBonus",
        "ImbueAttempts", "ImbueSuccesses", "CreatureKills", "PlayerKillsPk", "PlayerKillsPkl",
        "RaresTierOne", "RaresTierTwo", "RaresTierThree", "RaresTierFour", "RaresTierFive",
        "AugmentationStat", "AugmentationFamilyStat", "AugmentationInnateFamilyCount", "AugmentationInnateStrength", "AugmentationInnateEndurance",
        "AugmentationInnateCoordination", "AugmentationInnateQuickness", "AugmentationInnateFocus", "AugmentationInnateSelf", "AugmentationSpecializeSalvaging",
        "AugmentationSpecializeItemTinkering", "AugmentationSpecializeArmorTinkering", "AugmentationSpecializeMagicItemTinkering", "AugmentationSpecializeWeaponTinkering", "AugmentationExtraPackSlot",
        "AugmentationIncreasedCarryingCapacity", "AugmentationLessDeathItemLoss", "AugmentationSpellsRemainPastDeath", "AugmentationCriticalDefense", "AugmentationBonusXp",
        "AugmentationBonusSalvage", "AugmentationBonusImbueChance", "AugmentationFasterRegen", "AugmentationIncreasedSpellDuration", "AugmentationResistanceFamily",
        "AugmentationResistanceSlash", "AugmentationResistancePierce", "AugmentationResistanceBlunt", "AugmentationResistanceAcid", "AugmentationResistanceFire",
        "AugmentationResistanceFrost", "AugmentationResistanceLightning", "RaresTierOneLogin", "RaresTierTwoLogin", "RaresTierThreeLogin",
        "RaresTierFourLogin", "RaresTierFiveLogin", "RaresLoginTimestamp", "RaresTierSix", "RaresTierSeven",
        "RaresTierSixLogin", "RaresTierSevenLogin", "ItemAttributeLimit", "ItemAttributeLevelLimit", "ItemAttribute2ndLimit",
        "ItemAttribute2ndLevelLimit", "CharacterTitleId", "NumCharacterTitles", "ResistanceModifierType", "FreeTinkersBitfield",
        "EquipmentSetId", "PetClass", "Lifespan", "RemainingLifespan", "UseCreateQuantity",
        "WieldRequirements2", "WieldSkilltype2", "WieldDifficulty2", "WieldRequirements3", "WieldSkilltype3",
        "WieldDifficulty3", "WieldRequirements4", "WieldSkilltype4", "WieldDifficulty4", "Unique",
        "SharedCooldown", "Faction1Bits", "Faction2Bits", "Faction3Bits", "Hatred1Bits",
        "Hatred2Bits", "Hatred3Bits", "SocietyRankCelhan", "SocietyRankEldweb", "SocietyRankRadblo",
        "HearLocalSignals", "HearLocalSignalsRadius", "Cleaving", "AugmentationSpecializeGearcraft", "AugmentationInfusedCreatureMagic",
        "AugmentationInfusedItemMagic", "AugmentationInfusedLifeMagic", "AugmentationInfusedWarMagic", "AugmentationCriticalExpertise", "AugmentationCriticalPower",
        "AugmentationSkilledMelee", "AugmentationSkilledMissile", "AugmentationSkilledMagic", "ImbuedEffect2", "ImbuedEffect3",
        "ImbuedEffect4", "ImbuedEffect5", "DamageRating", "DamageResistRating", "AugmentationDamageBonus",
        "AugmentationDamageReduction", "ImbueStackingBits", "HealOverTime", "CritRating", "CritDamageRating",
        "CritResistRating", "CritDamageResistRating", "HealingResistRating", "DamageOverTime", "ItemMaxLevel",
        "ItemXpStyle", "EquipmentSetExtra", "AetheriaBitfield", "HealingBoostRating", "HeritageSpecificArmor",
        "AlternateRacialSkills", "AugmentationJackOfAllTrades", "AugmentationResistanceNether", "AugmentationInfusedVoidMagic", "WeaknessRating",
        "NetherOverTime", "NetherResistRating", "LuminanceAward", "LumAugDamageRating", "LumAugDamageReductionRating",
        "LumAugCritDamageRating", "LumAugCritReductionRating", "LumAugSurgeEffectRating", "LumAugSurgeChanceRating", "LumAugItemManaUsage",
        "LumAugItemManaGain", "LumAugVitality", "LumAugHealingRating", "LumAugSkilledCraft", "LumAugSkilledSpec",
        "LumAugNoDestroyCraft", "RestrictInteraction", "OlthoiLootTimestamp", "OlthoiLootStep", "UseCreatesContractId",
        "DotResistRating", "LifeResistRating", "CloakWeaveProc", "WeaponType", "MeleeMastery",
        "RangedMastery", "SneakAttackRating", "RecklessnessRating", "DeceptionRating", "CombatPetRange",
        "WeaponAuraDamage", "WeaponAuraSpeed", "SummoningMastery", "HeartbeatLifespan", "UseLevelRequirement",
        "LumAugAllSkills", "UseRequiresSkill", "UseRequiresSkillLevel", "UseRequiresSkillSpec", "UseRequiresLevel",
        "GearDamage", "GearDamageResist", "GearCrit", "GearCritResist", "GearCritDamage",
        "GearCritDamageResist", "GearHealingBoost", "GearNetherResist", "GearLifeResist", "GearMaxHealth",
        "Unknown380", "PkDamageRating", "PkDamageResistRating", "GearPkDamageRating", "GearPkDamageResistRating",
        "Unknown385Seen", "Overpower", "OverpowerResist", "GearOverpower", "GearOverpowerResist",
        "Enlightenment"
    };

    private static readonly string[] BoolNames =
    {
        "Undef", "Stuck", "Open", "Locked", "RotProof",
        "AllegianceUpdateRequest", "AiUsesMana", "AiUseHumanMagicAnimations", "AllowGive", "CurrentlyAttacking",
        "AttackerAi", "IgnoreCollisions", "ReportCollisions", "Ethereal", "GravityStatus",
        "LightsStatus", "ScriptedCollision", "Inelastic", "Visibility", "Attackable",
        "SafeSpellComponents", "AdvocateState", "Inscribable", "DestroyOnSell", "UiHidden",
        "IgnoreHouseBarriers", "HiddenAdmin", "PkWounder", "PkKiller", "NoCorpse",
        "UnderLifestoneProtection", "ItemManaUpdatePending", "GeneratorStatus", "ResetMessagePending", "DefaultOpen",
        "DefaultLocked", "DefaultOn", "OpenForBusiness", "IsFrozen", "DealMagicalItems",
        "LogoffImDead", "ReportCollisionsAsEnvironment", "AllowEdgeSlide", "AdvocateQuest", "IsAdmin",
        "IsArch", "IsSentinel", "IsAdvocate", "CurrentlyPoweringUp", "GeneratorEnteredWorld",
        "NeverFailCasting", "VendorService", "AiImmobile", "DamagedByCollisions", "IsDynamic",
        "IsHot", "IsAffecting", "AffectsAis", "SpellQueueActive", "GeneratorDisabled",
        "IsAcceptingTells", "LoggingChannel", "OpensAnyLock", "UnlimitedUse", "GeneratedTreasureItem",
        "IgnoreMagicResist", "IgnoreMagicArmor", "AiAllowTrade", "SpellComponentsRequired", "IsSellable",
        "IgnoreShieldsBySkill", "NoDraw", "ActivationUntargeted", "HouseHasGottenPriorityBootPos", "GeneratorAutomaticDestruction",
        "HouseHooksVisible", "HouseRequiresMonarch", "HouseHooksEnabled", "HouseNotifiedHudOfHookCount", "AiAcceptEverything",
        "IgnorePortalRestrictions", "RequiresBackpackSlot", "DontTurnOrMoveWhenGiving", "NpcLooksLikeObject", "IgnoreCloIcons",
        "AppraisalHasAllowedWielder", "ChestRegenOnClose", "LogoffInMinigame", "PortalShowDestination", "PortalIgnoresPkAttackTimer",
        "NpcInteractsSilently", "Retained", "IgnoreAuthor", "Limbo", "AppraisalHasAllowedActivator",
        "ExistedBeforeAllegianceXpChanges", "IsDeaf", "IsPsr", "Invincible", "Ivoryable",
        "Dyable", "CanGenerateRare", "CorpseGeneratedRare", "NonProjectileMagicImmune", "ActdReceivedItems",
        "ExecutingEmote", "FirstEnterWorldDone", "RecallsDisabled", "RareUsesTimer", "ActdPreorderReceivedItems",
        "Afk", "IsGagged", "ProcSpellSelfTargeted", "IsAllegianceGagged", "EquipmentSetTriggerPiece",
        "Uninscribe", "WieldOnUse", "ChestClearedWhenClosed", "NeverAttack", "SuppressGenerateEffect",
        "TreasureCorpse", "EquipmentSetAddLevel", "BarberActive", "TopLayerPriority", "NoHeldItemShown",
        "LoginAtLifestone", "OlthoiPk", "Account15Days", "HadNoVitae", "NoOlthoiTalk",
        "AutowieldLeft", "MergeLocked"
    };

    private static readonly string[] StringNames =
    {
        "Undef", "Name", "Title", "Sex", "HeritageGroup",
        "Template", "AttackersName", "Inscription", "ScribeName", "VendorsName",
        "Fellowship", "MonarchsName", "LockCode", "KeyCode", "Use",
        "ShortDesc", "LongDesc", "ActivationTalk", "UseMessage", "ItemHeritageGroupRestriction",
        "PluralName", "MonarchsTitle", "ActivationFailure", "ScribeAccount", "TownName",
        "CraftsmanName", "UsePkServerError", "ScoreCachedText", "ScoreDefaultEntryFormat", "ScoreFirstEntryFormat",
        "ScoreLastEntryFormat", "ScoreOnlyEntryFormat", "ScoreNoEntry", "Quest", "GeneratorEvent",
        "PatronsTitle", "HouseOwnerName", "QuestRestriction", "AppraisalPortalDestination", "TinkerName",
        "ImbuerName", "HouseOwnerAccount", "DisplayName", "DateOfBirth", "ThirdPartyApi",
        "KillQuest", "Afk", "AllegianceName", "AugmentationAddQuest", "KillQuest2",
        "KillQuest3", "UseSendsSignal", "GearPlatingNameString"
    };

    public static string GetIntName(uint id) =>
        id < (uint)IntNames.Length ? IntNames[id] : null;

    public static string GetBoolName(uint id) =>
        id < (uint)BoolNames.Length ? BoolNames[id] : null;

    public static string GetStringName(uint id) =>
        id < (uint)StringNames.Length ? StringNames[id] : null;
}

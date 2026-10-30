-- UB-style use of the enum globals (UtilityBelt.Common.Enums and the scripting enums).
-- Written the way UB scripts use them: comparisons against fields, flag helpers,
-- ToNumber(), tostring() for display, and the property-id enums passed to wo:Value().

-- Globals UB scripts expect to exist
for _, name in ipairs({ "ObjectClass", "ObjectType", "SkillId", "AttributeId", "VitalId", "CombatMode",
    "IntId", "Int64Id", "FloatId", "StringId", "BoolId", "DataId", "InstanceId", "EquipMask",
    "ChatMessageType", "ChatChannel", "SkillTrainingType", "MagicSchool", "ClientState", "DistanceType",
    "AddRemoveEventType", "ActionError", "ActionType", "UseActionType", "CoverageMask", "ClientCapability",
    "TradeSide", "CharacterOptions1", "MaterialType", "DamageType", "CreatureType", "WeenieError" }) do
  check(_G[name] ~= nil, "global enum " .. name)
end

-- Values that match UB's numbering
eq(ObjectClass.Monster, 5, "ObjectClass.Monster")
eq(ObjectClass.NumObjectClasses, 43, "ObjectClass.NumObjectClasses")
eq(SkillId.MeleeDefense, 6, "SkillId.MeleeDefense")
eq(SkillId.MissleWeapons, 47, "SkillId.MissleWeapons (UB's spelling)")
eq(SkillId.ThrownWeapons, 12, "SkillId.ThrownWeapons")
eq(SkillId.ArmorRepair, 26, "SkillId.ArmorRepair")
eq(SkillId.Undef, 0, "SkillId.Undef")
eq(AttributeId.Undef, 0, "AttributeId.Undef")
eq(VitalId.Health, 1, "VitalId.Health")
eq(VitalId.Stamina, 3, "VitalId.Stamina (UB numbering)")
eq(VitalId.Mana, 5, "VitalId.Mana (UB numbering)")
eq(CombatMode.Magic, 8, "CombatMode.Magic")
eq(CombatMode.Invalid, 153, "CombatMode.Invalid")
eq(EquipMask.Head, 1, "EquipMask.Head")
eq(EquipMask.MeleeWeapon, 1048576, "EquipMask.MeleeWeapon")
eq(ChatMessageType.Tell, 3, "ChatMessageType.Tell")
eq(ChatMessageType.Fellowship, 19, "ChatMessageType.Fellowship")
eq(ChatChannel.General, 2, "ChatChannel.General")
eq(SkillTrainingType.Specialized, 3, "SkillTrainingType.Specialized")
eq(MagicSchool.WarMagic, 1, "MagicSchool.WarMagic")
eq(DistanceType.T2D, 0, "DistanceType.T2D")
eq(AddRemoveEventType.Removed, 1, "AddRemoveEventType.Removed")
eq(ActionType.Inventory, 9000, "ActionType.Inventory")
eq(IntId.Value, 19, "IntId.Value")
eq(IntId.MerchandiseObjectTypes, 74, "IntId.MerchandiseObjectTypes (UB name)")
eq(UseActionType.SummonPortal, 2, "UseActionType.SummonPortal")
eq(TradeSide.Partner, 2, "TradeSide.Partner")
eq(MaterialType.Steel, 64, "MaterialType.Steel")
eq(DamageType.Fire, 16, "DamageType.Fire")

-- ToNumber() on any enum value, as UB scripts do before arithmetic
eq(SkillId.Axe:ToNumber(), 1, "SkillId.Axe:ToNumber()")
eq(CombatMode.Melee:ToNumber() + 1, 3, "CombatMode.Melee:ToNumber() + 1")
eq(StringId.Name:ToNumber(), 1, "StringId.Name:ToNumber()")
eq(ObjectType.Creature:ToNumber(), 16, "ObjectType.Creature:ToNumber()")

-- tostring() of the typed enums gives the name (UB prints enum values by name)
eq(tostring(StringId.Name), "Name", "tostring(StringId.Name)")
eq(tostring(FloatId.WeaponLength), "WeaponLength", "tostring(FloatId.WeaponLength)")
eq(tostring(ObjectType.Creature), "Creature", "tostring(ObjectType.Creature)")

-- Flag helpers on flag enums
local both = ObjectType.Creature:AddFlags(ObjectType.Misc)
check(both:HasFlags(ObjectType.Creature), "AddFlags/HasFlags Creature")
check(both:HasFlags(ObjectType.Misc), "AddFlags/HasFlags Misc")
check(not both:HasFlags(ObjectType.Portal), "HasFlags Portal is false")
eq(both:ToNumber(), 16 + 128, "combined flags value")
local back = both:RemoveFlags(ObjectType.Misc)
check(back == ObjectType.Creature, "RemoveFlags gives the same enum value back")
check(EquipMask.Head:HasFlags(EquipMask.Head), "HasFlags on a numeric flag enum")

-- Typed enums compare with each other, not with plain numbers (UB userdata behaves so too)
check(StringId.Name == StringId.Name, "same typed value is equal")
check(StringId.Name ~= StringId.Title, "different typed values differ")
check(FloatId.WeaponLength < FloatId.DamageMod, "typed values order by number")

-- Reverse lookup of a number to its name (RynthLua's way; UB uses tostring)
eq(SkillId[6], "MeleeDefense", "SkillId[6]")
eq(ObjectClass[5], "Monster", "ObjectClass[5]")

-- ClientState: UB compares game.State and OnStateChanged.NewState against it
check(game.State == ClientState.In_Game, "game.State == ClientState.In_Game")
check(ClientState.Character_Select_Screen ~= ClientState.In_Game, "ClientState values differ")

-- ActionError compares with the action's Error
eq(ActionError.TooBusy, ActionError.TooBusy, "ActionError.TooBusy")

done()

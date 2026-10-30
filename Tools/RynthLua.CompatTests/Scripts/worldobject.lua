-- UB-style WorldObject use: typed Value() reads, HasValue, list properties, positions,
-- distances, parents, enum-valued fields and the per-object events.
local ids = harness.ids

local sword = game.World.Get(ids.Sword)
check(sword ~= nil, "World.Get(sword)")
eq(sword.Id, ids.Sword, "Id")
eq(sword.Name, "Plain Sword", "Name")
eq(sword.WeenieClassId, 351, "WeenieClassId")
check(sword.ObjectClass == ObjectClass.MeleeWeapon, "ObjectClass == ObjectClass.MeleeWeapon")
check(sword.ObjectType == ObjectType.MeleeWeapon, "ObjectType == ObjectType.MeleeWeapon")
check(sword.ObjectType:HasFlags(ObjectType.MeleeWeapon), "ObjectType:HasFlags")

-- Value(key [, default]) picks the property kind from the key's enum
eq(sword:Value(IntId.Value), 500, "Value(IntId.Value)")
eq(sword:Value(StringId.Name), "Plain Sword", "Value(StringId.Name)")
eq(sword:Value(StringId.LongDesc), "A plain sword.", "Value(StringId.LongDesc)")
eq(sword:Value(FloatId.WeaponLength), 1.25, "Value(FloatId.WeaponLength)")
eq(sword:Value(BoolId.Inscribable), true, "Value(BoolId.Inscribable)")
eq(sword:Value(DataId.Icon), 0x06001234, "Value(DataId.Icon)")
eq(sword:Value(Int64Id.ItemTotalXp), 0, "Value(Int64Id.ItemTotalXp)")
eq(sword:Value(InstanceId.Container), game.Character.Id, "Value(InstanceId.Container)")
-- API v73: every instance id, not just Container/Wielder
eq(game.Character.Weenie:Value(InstanceId.Monarch), ids.OtherPlayer, "Value(InstanceId.Monarch) on the character")
check(game.Character.Weenie:HasValue(InstanceId.Monarch), "HasValue(InstanceId.Monarch)")
check(not sword:HasValue(InstanceId.Wielder), "an unwielded sword has no Wielder")
eq(sword:Value(InstanceId.PetOwner), 0, "missing instance id defaults to 0")
-- defaults when the object doesn't have the property
eq(sword:Value(IntId.ArmorLevel), 0, "missing int defaults to 0")
eq(sword:Value(IntId.ArmorLevel, 7), 7, "missing int uses the given default")
eq(sword:Value(StringId.Inscription), "", "missing string defaults to empty")
eq(sword:Value(BoolId.Stuck), false, "missing bool defaults to false")
eq(sword:Value(FloatId.DamageMod, 1.5), 1.5, "missing float uses the given default")
-- HasValue
check(sword:HasValue(IntId.Value), "HasValue(IntId.Value)")
check(not sword:HasValue(IntId.ArmorLevel), "not HasValue(IntId.ArmorLevel)")
check(sword:HasValue(StringId.LongDesc), "HasValue(StringId.LongDesc)")
check(not sword:HasValue(FloatId.DamageMod), "not HasValue(FloatId.DamageMod)")
-- the enum-typed fields
eq(sword.ValidWieldedLocations, EquipMask.MeleeWeapon, "ValidWieldedLocations")
eq(sword.Material, MaterialType.Steel, "Material")
eq(sword.Burden, 0, "Burden (none set)")
check(sword.Container ~= nil and sword.Container.Id == game.Character.Id, "Container is the character")
eq(sword.ContainerId, game.Character.Id, "ContainerId")
eq(sword.WielderId, 0, "WielderId")
check(sword:GetTopmostParent().Id == game.Character.Id, "GetTopmostParent of a pack item")

-- a pack in a pack
local stone = game.World.Get(ids.ManaStone)
eq(stone.ContainerId, ids.Backpack, "stone in the backpack")
eq(stone:GetTopmostParent().Id, game.Character.Id, "GetTopmostParent through a side pack")

-- list properties: Items / ItemIds / Containers / ContainerIds / AllItems / Equipment
local me = game.Character.Weenie
local itemNames = {}
for _, wo in ipairs(me.Items) do itemNames[wo.Name] = true end
check(itemNames["Healing Potion"] and itemNames["Plain Sword"], "Weenie.Items has pack items")
check(not itemNames["Backpack"], "Weenie.Items has no containers")
check(not itemNames["Mana Stone"], "Weenie.Items is only the main pack")
eq(#me.ContainerIds, 1, "one side pack")
eq(me.Containers[1].Name, "Backpack", "Containers[1]")
local all = {}
for _, wo in ipairs(me.AllItems) do all[wo.Name] = true end
check(all["Mana Stone"] and all["Prismatic Taper"] and all["Healing Potion"], "AllItems includes side-pack items")
local eqNames = {}
for _, wo in ipairs(me.Equipment) do eqNames[wo.Name] = true end
check(eqNames["Wand of Testing"] and eqNames["Leather Cap"], "Equipment")
eq(#me.EquipmentIds, 2, "EquipmentIds")
local backpack = game.World.Get(ids.Backpack)
eq(#backpack.Items, 2, "backpack.Items")
eq(#backpack.ItemIds, 2, "backpack.ItemIds")
-- the old call form still works
eq(#backpack:Items(), 2, "backpack:Items() still callable")
-- iterating a list with a plain generic for
local n = 0
for wo in backpack.Items do n = n + 1; check(wo.Name ~= nil, "for-in item has a name") end
eq(n, 2, "for wo in list do")

-- equipment slot
local cap = game.World.Get(ids.Cap)
check(cap.CurrentWieldedLocation == EquipMask.Head, "CurrentWieldedLocation == EquipMask.Head")
eq(cap.WielderId, game.Character.Id, "cap WielderId")
eq(cap.Wielder.Id, game.Character.Id, "cap Wielder")
local wand = game.World.Get(ids.Wand)
eq(#wand.SpellIds, 1, "wand SpellIds")
eq(wand.SpellIds[1].Id, 2345, "wand SpellIds[1].Id")
eq(wand.SpellId, 2345, "wand SpellId")

-- positions and distances
local drudge = game.World.Get(ids.Drudge)
local pos = drudge.ServerPosition
check(pos ~= nil, "ServerPosition")
eq(pos.Landcell, 0x7D640013, "ServerPosition.Landcell")
eq(pos.Frame.Origin.X, 110, "Frame.Origin.X")
eq(pos.Frame.Origin.Z, 10, "Frame.Origin.Z")
check(pos.Frame.Orientation ~= nil and pos.Frame.Orientation.W ~= nil, "Frame.Orientation")
local mePos = me.ServerPosition
check(math.abs(mePos:DistanceTo3D(pos) - 10) < 0.01, "Position:DistanceTo3D")
check(math.abs(me:DistanceTo3D(drudge) - 10) < 0.01, "DistanceTo3D(wo)")
check(math.abs(me:DistanceTo3D(ids.Drudge) - 10) < 0.01, "DistanceTo3D(id)")
local portal = game.World.Get(ids.PortalObj)
check(math.abs(me:DistanceTo2D(portal) - 20) < 0.01, "DistanceTo2D(wo) ignores height")
check(math.abs(me:DistanceTo2d(ids.PortalObj) - 20) < 0.01, "DistanceTo2d(id)")
check(math.abs(me:DistanceTo3D(portal) - 36.0555) < 0.01, "DistanceTo3D includes height")
check(sword.ServerPosition == nil, "pack items have no ServerPosition")
check(ObjectDescriptionFlag ~= nil, "ObjectDescriptionFlag enum")
eq(portal.ObjectDescriptionFlag, 0x40000, "ObjectDescriptionFlag (the description bitfield)")
check(PhysicsState ~= nil, "PhysicsState enum")
eq(portal.PhysicsState, 0x0C, "PhysicsState (the last state the server sent)")
eq(sword.PhysicsState, 0, "PhysicsState 0 when none was seen")

-- the player's stats on the weenie, the way UB keeps them
eq(me.Vitals[VitalId.Health].Current, 300, "Weenie.Vitals[VitalId.Health].Current")
eq(me.Vitals[VitalId.Mana].Max, 500, "Weenie.Vitals[VitalId.Mana].Max")
eq(me.Vitals[VitalId.Stamina].Type, VitalId.Stamina, "Vital.Type")
eq(me.Skills[SkillId.MeleeDefense].Current, 400, "Weenie.Skills[...].Current")
eq(me.Skills[SkillId.MeleeDefense].Training, SkillTrainingType.Specialized, "Skill.Training")
eq(me.Skills[SkillId.MeleeDefense].Type, SkillId.MeleeDefense, "Skill.Type")
eq(me.Attributes[AttributeId.Strength].Base, 190, "Weenie.Attributes[...].Base")
eq(me.Attributes[AttributeId.Strength].Type, AttributeId.Strength, "Attribute.Type")

-- tostring and equality
check(tostring(sword):find("Plain Sword") ~= nil, "tostring(wo)")
check(game.World.Get(ids.Sword) == sword, "two wrappers of one object are ==")

-- per-object events: OnPositionChanged and OnDestroyed
local moved, destroyed = nil, false
drudge.OnPositionChanged.Add(function(e) moved = e end)
harness.move(ids.Drudge, 5, 0, 0)
local t = 0
while moved == nil and t < 3000 do sleep(20); t = t + 20 end
check(moved ~= nil, "OnPositionChanged fired")
if moved then
  eq(moved.Weenie.Id, ids.Drudge, "OnPositionChanged.Weenie")
  eq(moved.Position.Frame.Origin.X, 115, "OnPositionChanged.Position")
end
drudge.OnDestroyed.Add(function(e) destroyed = e.ObjectId == ids.Drudge end)
harness.delete(ids.Drudge)
t = 0
while not destroyed and t < 3000 do sleep(20); t = t + 20 end
check(destroyed, "OnDestroyed fired with ObjectId")

done()

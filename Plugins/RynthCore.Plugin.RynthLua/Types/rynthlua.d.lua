---@meta _
-- RynthLua API type definitions for the Lua language server (LuaLS / sumneko, the
-- "Lua" extension in VS Code). Definitions only: nothing here runs in the game.
-- See Types/README.md for the .luarc.json that points the editor at this folder,
-- and docs/RYNTHLUA.md in RynthSuite for the full reference.
--
-- Calling style: API tables (game.World, game.Actions, storage, json, ...) are
-- declared with dot calls, WorldObject methods with colon calls. At run time both
-- work everywhere (`wo.Use()` and `wo:Use()` are the same).
--
-- Events also take UB's verbs in lower case (evt.add, evt.once, evt.until, evt.remove).
-- Lists (T[] below) are 1-based tables that can also be iterated directly
-- (`for wo in list do`) and called (a call returns the list).

------------------------------------------------------------------------------
-- Shared aliases
------------------------------------------------------------------------------

---An object id, or a WorldObject (its Id is used).
---@alias RynthLua.ObjectRef RynthLua.WorldObject|integer

---A search filter: a name substring, an ObjectClass value, an ObjectType value (game.World
---only), or a function. In game.World a string that is exactly an ObjectClass name
---("Monster", "Portal") means that class; in game.Character it is always a name substring.
---@alias RynthLua.Filter string|ObjectClass|integer|RynthLua.EnumValue|fun(wo: RynthLua.WorldObject): boolean

---@alias RynthLua.GameState
---| "CharacterSelect"
---| "InGame"
---| "LoggingOut"

---@alias RynthLua.CombatModeName "NonCombat"|"Melee"|"Missile"|"Magic"|"Undef"
---@alias RynthLua.TrainingName "Unusable"|"Untrained"|"Trained"|"Specialized"
---@alias RynthLua.VitalName "Health"|"Stamina"|"Mana"

---A typed enum value (Int64Id, FloatId, StringId, BoolId, DataId, InstanceId, ObjectType):
---tostring gives its name; it compares and orders with values of its enum (not with plain
---numbers) and is accepted wherever a number is.
---@class RynthLua.EnumValue
local EnumValue = {}
---@return integer
function EnumValue:ToNumber() end
---True when every given flag is set.
---@param ... RynthLua.EnumValue|integer
---@return boolean
function EnumValue:HasFlags(...) end
---@param ... RynthLua.EnumValue|integer
---@return RynthLua.EnumValue
function EnumValue:AddFlags(...) end
---@param ... RynthLua.EnumValue|integer
---@return RynthLua.EnumValue
function EnumValue:RemoveFlags(...) end

------------------------------------------------------------------------------
-- Scheduler and script globals
------------------------------------------------------------------------------

---Parks the calling thread for `ms` milliseconds. The game keeps running.
---@param ms? number
function sleep(ms) end

---Same as sleep (v1 name).
---@param ms? number
function wait(ms) end

---Parks the calling thread until the action has finished, then returns it.
---@generic T : RynthLua.Action
---@param action T
---@return T
function await(action) end

---Checks `cond()` every 100 ms until it returns true. False after `timeoutMs`.
---@param cond fun(): any
---@param timeoutMs? number
---@return boolean
function waitfor(cond, timeoutMs) end

---Runs `fn(...)` as a new thread of this script.
---@param fn function
---@param ... any
function spawn(fn, ...) end

---Stops this script (event handlers, commands and queued actions included).
function stop() end

---Milliseconds since Windows started (the clock actions' StartedAt / FinishedAt use).
---@return integer
function clock() end

---@alias RynthLua.V1Event
---| "login"            # ()
---| "logout"           # ()
---| "chat"             # (text, chatType)
---| "target"           # (currentId, previousId)
---| "combatmode"       # (currentMode, previousMode)
---| "objectcreated"    # (objectId)
---| "objectdeleted"    # (objectId)
---| "health"           # (targetId, ratio, current, max)
---| "damagedealt"      # (damage, damageType, crit)
---| "damagetaken"      # (damage, damageType, crit)
---| "kill"             # (deathMessage)
---| "enchantmentadded" # (spellId, durationSeconds)
---| "enchantmentremoved" # (enchantmentId)
---| "vendoropen"       # (vendorId)
---| "vendorclose"      # (vendorId)

---v1 events: calls `fn` (as its own thread) with the event's arguments.
---@param event RynthLua.V1Event
---@param fn function
function on(event, fn) end

---Removes one v1 handler, or every handler of the event when `fn` is left out.
---@param event RynthLua.V1Event
---@param fn? function
function off(event, fn) end

-- require(name) is Lua's own as far as the editor is concerned (so module resolution keeps
-- working): built-ins "json", "storage", "enums", "rynth.ai", "filesystem", "rynthlua", or a
-- .lua file in the script's folder (never outside it). The stubs next to this file type them.

------------------------------------------------------------------------------
-- v1 globals (kept for scripts written for RynthAi's old Lua)
------------------------------------------------------------------------------

---Writes to your own chat window (nobody else sees it).
---@param ... any
function chat(...) end

---Runs a line as if typed in chat (/ra, /ub, /say, ...).
---@param ... any
function command(...) end

---Evaluates a meta expression (needs RynthAi). Numbers come back as numbers.
---@param expression string
---@return number|string
function expr(expression) end

---A meta session variable (shared with metas; needs RynthAi).
---@param name string
---@return number|string
function getvar(name) end

---Sets a meta session variable (needs RynthAi).
---@param name string
---@param value any
---@return number|string
function setvar(name, value) end

---Any meta expression function by name: `meta.wobjectgetname(id)` (needs RynthAi).
---@type table<string, fun(...): number|string>
meta = {}

---@class RynthLua.Me
---@field name string
---@field id integer
---@field health number
---@field maxhealth number
---@field stamina number
---@field maxstamina number
---@field mana number
---@field maxmana number
---@field landcell number
---@field coords string

---v1 shortcuts for your character (read live; most need RynthAi).
---@type RynthLua.Me
me = {}

---@class RynthLua.V1RynthAi
local V1RynthAi = {}
---Walk to a spot: numbers or "33.5N" / "44.8W".
---@param ns number|string
---@param ew number|string
function V1RynthAi:GoTo(ns, ew) end
function V1RynthAi:Stop() end
---Uses the nearest portal or NPC whose name contains `name`.
---@param name string
---@return boolean
function V1RynthAi:UsePortal(name) end
---Distance to the nearest object whose name contains `name` (-1 when none).
---@param name string
---@return number
function V1RynthAi:GetDistance(name) end
---@return number
function V1RynthAi:GetPlayerNS() end
---@return number
function V1RynthAi:GetPlayerEW() end
---@return number
function V1RynthAi:GetPlayerZ() end
---@param target integer|string
---@return boolean
function V1RynthAi:IsPathClear(target) end

---v1 RynthAi calls (colon syntax: `RynthAi:GoTo("33.5N", "44.8W")`).
---@type RynthLua.V1RynthAi
RynthAi = {}

---Any RynthAi setting by name, read or assigned (needs RynthAi).
---@type table<string, any>
Settings = {}

------------------------------------------------------------------------------
-- Events
------------------------------------------------------------------------------

---@class RynthLua.EmptyArgs

---@class RynthLua.StateChangedArgs
---@field NewState RynthLua.GameState

---@class RynthLua.ObjectCreatedArgs
---@field ObjectId integer
---@field Object RynthLua.WorldObject

---@class RynthLua.ObjectReleasedArgs
---@field ObjectId integer

---UtilityBelt's ChatEventArgs.
---@class RynthLua.ChatTextArgs
---@field Message string      The whole line (OnTell/OnLocalMessage/OnChannelMessage/OnFellowMessage: the text inside the quotes).
---@field Type ChatType|integer   A ChatMessageType number.
---@field SenderName string   "" unless the line is "X says/tells you, ...".
---@field SenderId integer    0 unless the line carries a name link.
---@field Room ChatChannel|integer  The chat room of "[General] ..." lines, else 0.
---@field Eat boolean         Always false (incoming lines can't be eaten).

---@alias RynthLua.TellArgs RynthLua.ChatTextArgs

---@class RynthLua.ChatInputArgs
---@field Text string         The typed line.
---@field Eat boolean          Set true on a "/word ..." line: from then on that command word is eaten as it is typed.

---@class RynthLua.ContainerArgs
---@field Container RynthLua.WorldObject

---@class RynthLua.PositionChangedArgs
---@field Weenie RynthLua.WorldObject
---@field Position? RynthLua.ServerPosition

---@class RynthLua.LuminanceChangedArgs
---@field AvailableLuminance integer
---@field OldAvailableLuminance integer

---@class RynthLua.TradeArgs
---@field InitiatorId? integer      OnStarted
---@field PartnerId? integer        OnStarted, OnCompleted
---@field ObjectId? integer         OnObjectAdded, OnFailedToAddObject
---@field Side? TradeSide|integer   OnObjectAdded: 1 yours, 2 the partner's
---@field AcceptorId? integer       OnAccepted
---@field DeclinerId? integer       OnDeclined
---@field ResetterObjectId? integer OnReset
---@field Reason? integer           OnFailedToAddObject (a WeenieError), OnEnded (1 normal, 2 combat, 0x51 cancelled)
---@field YourItemIds? integer[]    OnCompleted
---@field PartnerItemIds? integer[] OnCompleted

---@class RynthLua.SelectedArgs
---@field ObjectId integer    0 when the selection was cleared.
---@field PreviousId integer
---@field Object? RynthLua.WorldObject

---@class RynthLua.VitalChangedArgs
---@field Type VitalId|integer    Health 1, Stamina 3, Mana 5.
---@field TypeName RynthLua.VitalName
---@field Value integer
---@field OldValue integer

---@class RynthLua.DeathArgs
---@field Text string         The death line from chat, or "".
---@field KillerId integer    Always 0 (not known).

---@class RynthLua.CombatModeChangedArgs
---@field NewMode CombatMode|integer
---@field OldMode CombatMode|integer
---@field NewModeName RynthLua.CombatModeName
---@field OldModeName RynthLua.CombatModeName

---@class RynthLua.EnchantmentsChangedArgs
---@field Type integer        0 = Added, 1 = Removed (UtilityBelt's AddRemoveEventType).
---@field TypeName "Added"|"Removed"
---@field SpellId integer
---@field LayeredSpellId { Id: integer }
---@field Name? string        Needs RynthAi.
---@field Refreshed boolean   Added again before it ran out (a recast).
---@field Enchantment RynthLua.Enchantment

---@class RynthLua.LevelChangedArgs
---@field Level integer
---@field OldLevel integer

---@class RynthLua.VitaeChangedArgs
---@field Vitae number        1.0 = no vitae penalty.
---@field OldVitae number

---@class RynthLua.TotalExperienceChangedArgs
---@field TotalExperience integer
---@field OldTotalExperience integer

---@class RynthLua.Event.Empty
---@field Add fun(handler: fun(e: RynthLua.EmptyArgs))
---@field Once fun(handler: fun(e: RynthLua.EmptyArgs))
---@field Until fun(handler: fun(e: RynthLua.EmptyArgs): boolean?)
---@field Remove fun(handler: function)

---@class RynthLua.Event.StateChanged
---@field Add fun(handler: fun(e: RynthLua.StateChangedArgs))
---@field Once fun(handler: fun(e: RynthLua.StateChangedArgs))
---@field Until fun(handler: fun(e: RynthLua.StateChangedArgs): boolean?)
---@field Remove fun(handler: function)

---@class RynthLua.Event.ObjectCreated
---@field Add fun(handler: fun(e: RynthLua.ObjectCreatedArgs))
---@field Once fun(handler: fun(e: RynthLua.ObjectCreatedArgs))
---@field Until fun(handler: fun(e: RynthLua.ObjectCreatedArgs): boolean?)
---@field Remove fun(handler: function)

---@class RynthLua.Event.ObjectReleased
---@field Add fun(handler: fun(e: RynthLua.ObjectReleasedArgs))
---@field Once fun(handler: fun(e: RynthLua.ObjectReleasedArgs))
---@field Until fun(handler: fun(e: RynthLua.ObjectReleasedArgs): boolean?)
---@field Remove fun(handler: function)

---@class RynthLua.Event.ChatText
---@field Add fun(handler: fun(e: RynthLua.ChatTextArgs))
---@field Once fun(handler: fun(e: RynthLua.ChatTextArgs))
---@field Until fun(handler: fun(e: RynthLua.ChatTextArgs): boolean?)
---@field Remove fun(handler: function)

---@class RynthLua.Event.Tell
---@field Add fun(handler: fun(e: RynthLua.TellArgs))
---@field Once fun(handler: fun(e: RynthLua.TellArgs))
---@field Until fun(handler: fun(e: RynthLua.TellArgs): boolean?)
---@field Remove fun(handler: function)

---@class RynthLua.Event.Selected
---@field Add fun(handler: fun(e: RynthLua.SelectedArgs))
---@field Once fun(handler: fun(e: RynthLua.SelectedArgs))
---@field Until fun(handler: fun(e: RynthLua.SelectedArgs): boolean?)
---@field Remove fun(handler: function)

---@class RynthLua.Event.VitalChanged
---@field Add fun(handler: fun(e: RynthLua.VitalChangedArgs))
---@field Once fun(handler: fun(e: RynthLua.VitalChangedArgs))
---@field Until fun(handler: fun(e: RynthLua.VitalChangedArgs): boolean?)
---@field Remove fun(handler: function)

---@class RynthLua.Event.Death
---@field Add fun(handler: fun(e: RynthLua.DeathArgs))
---@field Once fun(handler: fun(e: RynthLua.DeathArgs))
---@field Until fun(handler: fun(e: RynthLua.DeathArgs): boolean?)
---@field Remove fun(handler: function)

---@class RynthLua.Event.CombatModeChanged
---@field Add fun(handler: fun(e: RynthLua.CombatModeChangedArgs))
---@field Once fun(handler: fun(e: RynthLua.CombatModeChangedArgs))
---@field Until fun(handler: fun(e: RynthLua.CombatModeChangedArgs): boolean?)
---@field Remove fun(handler: function)

---@class RynthLua.Event.EnchantmentsChanged
---@field Add fun(handler: fun(e: RynthLua.EnchantmentsChangedArgs))
---@field Once fun(handler: fun(e: RynthLua.EnchantmentsChangedArgs))
---@field Until fun(handler: fun(e: RynthLua.EnchantmentsChangedArgs): boolean?)
---@field Remove fun(handler: function)

---@class RynthLua.Event.LevelChanged
---@field Add fun(handler: fun(e: RynthLua.LevelChangedArgs))
---@field Once fun(handler: fun(e: RynthLua.LevelChangedArgs))
---@field Until fun(handler: fun(e: RynthLua.LevelChangedArgs): boolean?)
---@field Remove fun(handler: function)

---@class RynthLua.Event.VitaeChanged
---@field Add fun(handler: fun(e: RynthLua.VitaeChangedArgs))
---@field Once fun(handler: fun(e: RynthLua.VitaeChangedArgs))
---@field Until fun(handler: fun(e: RynthLua.VitaeChangedArgs): boolean?)
---@field Remove fun(handler: function)

---@class RynthLua.Event.TotalExperienceChanged
---@field Add fun(handler: fun(e: RynthLua.TotalExperienceChangedArgs))
---@field Once fun(handler: fun(e: RynthLua.TotalExperienceChangedArgs))
---@field Until fun(handler: fun(e: RynthLua.TotalExperienceChangedArgs): boolean?)
---@field Remove fun(handler: function)

---@class RynthLua.Event.ChatInput
---@field Add fun(handler: fun(e: RynthLua.ChatInputArgs))
---@field Once fun(handler: fun(e: RynthLua.ChatInputArgs))
---@field Until fun(handler: fun(e: RynthLua.ChatInputArgs): boolean?)
---@field Remove fun(handler: function)

---@class RynthLua.Event.Container
---@field Add fun(handler: fun(e: RynthLua.ContainerArgs))
---@field Once fun(handler: fun(e: RynthLua.ContainerArgs))
---@field Until fun(handler: fun(e: RynthLua.ContainerArgs): boolean?)
---@field Remove fun(handler: function)

---@class RynthLua.Event.PositionChanged
---@field Add fun(handler: fun(e: RynthLua.PositionChangedArgs))
---@field Once fun(handler: fun(e: RynthLua.PositionChangedArgs))
---@field Until fun(handler: fun(e: RynthLua.PositionChangedArgs): boolean?)
---@field Remove fun(handler: function)

---@class RynthLua.Event.LuminanceChanged
---@field Add fun(handler: fun(e: RynthLua.LuminanceChangedArgs))
---@field Once fun(handler: fun(e: RynthLua.LuminanceChangedArgs))
---@field Until fun(handler: fun(e: RynthLua.LuminanceChangedArgs): boolean?)
---@field Remove fun(handler: function)

---@class RynthLua.Event.Trade
---@field Add fun(handler: fun(e: RynthLua.TradeArgs))
---@field Once fun(handler: fun(e: RynthLua.TradeArgs))
---@field Until fun(handler: fun(e: RynthLua.TradeArgs): boolean?)
---@field Remove fun(handler: function)


------------------------------------------------------------------------------
-- game
------------------------------------------------------------------------------

---@class RynthLua.Game
---@field ServerName string
---@field AccountName string
---@field CharacterId integer          0 before login.
---@field State RynthLua.GameState
---@field LastTick integer             clock() at the start of the current tick.
---@field ScriptName string            This script's name.
---@field Character RynthLua.Character
---@field World RynthLua.World
---@field Actions RynthLua.Actions
---@field ActionQueue RynthLua.ActionQueue
---@field OnTick RynthLua.Event.Empty          Every plugin tick (prefer a loop with sleep).
---@field OnStateChanged RynthLua.Event.StateChanged
---@field OnScriptEnd RynthLua.Event.Empty     This script is being stopped (a short slice of time to save).
---@field Capabilities RynthLua.Capabilities
local Game = {}

---UB's client capabilities.
---@class RynthLua.Capabilities
local Capabilities = {}
---@param capability ClientCapability|integer   ClientCapability.ImGui: the engine can show script windows.
---@return boolean
function Capabilities.Has(capability) end
---@return integer
function Capabilities.Get() end

---A chat command owned by this script. A typed line starting with it is eaten and
---`fn(args)` runs on the next tick with the rest of the line.
---In a Decal (bridge-mode) client the engine only passes a few commands to plugins
---(/rc /ra /rv /rn /rnav /lua), so a bare "/name" never reaches this script there:
---players type `/lua name ...` instead (same handler, same args). Built-in /lua verbs
---(list, start, stop, ...) win over a script command of the same name.
---@param command string   "/name" (the slash is added when missing)
---@param fn fun(args: string)
function Game.RegisterCommand(command, fn) end

---@param command string
function Game.UnregisterCommand(command) end

---@type RynthLua.Game
game = {}

------------------------------------------------------------------------------
-- WorldObject
------------------------------------------------------------------------------

---@class RynthLua.Position
---@field Landcell integer
---@field X number
---@field Y number
---@field Z number
---@field NS? number   Map coordinate, north positive (only meaningful outdoors).
---@field EW? number   Map coordinate, east positive.

---UtilityBelt's Position.
---@class RynthLua.ServerPosition
---@field Landcell integer
---@field Frame { Origin: { X: number, Y: number, Z: number }, Orientation: { W: number, X: number, Y: number, Z: number } }
local ServerPosition = {}
---@param other RynthLua.ServerPosition
---@return number
function ServerPosition:DistanceTo2D(other) end
---@param other RynthLua.ServerPosition
---@return number
function ServerPosition:DistanceTo3D(other) end

---A world object. Every property is read live from the client when you index it.
---@class RynthLua.WorldObject
---@field Id integer
---@field Name string
---@field WeenieClassId integer
---@field ObjectClass ObjectClass|integer        Compare with ObjectClass.X.
---@field ObjectClassName string                 "Monster", "Portal", ...
---@field ObjectType RynthLua.EnumValue          AC ItemType flags as an ObjectType value (== ObjectType.X, :HasFlags).
---@field ContainerId integer
---@field WielderId integer
---@field Container? RynthLua.WorldObject
---@field Wielder? RynthLua.WorldObject
---@field CurrentWieldedLocation EquipMask|integer
---@field ValidWieldedLocations EquipMask|integer
---@field ClothingPriority CoverageMask|integer
---@field Material MaterialType|integer
---@field AmmoType AmmoType|integer
---@field TargetType integer
---@field CombatUse WieldType|integer
---@field RadarColor integer
---@field RadarBehavior integer
---@field IconEffects integer
---@field ObjectDescriptionFlag ObjectDescriptionFlag|integer   The description bitfield.
---@field PhysicsState PhysicsState|integer   The last state the server sent (0: none seen).
---@field CombatMode CombatMode|integer
---@field Burden integer
---@field StackSize integer                      1 for things that don't stack.
---@field HasAppraisalData boolean
---@field ItemIds integer[]                      What it holds directly (not side packs).
---@field Items RynthLua.WorldObject[]
---@field ContainerIds integer[]                 The side packs it holds.
---@field Containers RynthLua.WorldObject[]
---@field AllItemIds integer[]                   Every item, side packs' contents included.
---@field AllItems RynthLua.WorldObject[]
---@field EquipmentIds integer[]                 What it wields and wears.
---@field Equipment RynthLua.WorldObject[]
---@field SpellId integer
---@field SpellIds { Id: integer }[]
---@field EnchantmentIds { Id: integer }[]
---@field ServerPosition? RynthLua.ServerPosition   nil when the object has no position.
---@field Position? RynthLua.Position            nil when the object has no position (in a pack).
---@field Vitals RynthLua.Vitals                 Your own weenie only (empty on others).
---@field Skills RynthLua.Skills                 Your own weenie only.
---@field Attributes RynthLua.Attributes         Your own weenie only.
---@field OnPositionChanged RynthLua.Event.PositionChanged
---@field OnDestroyed RynthLua.Event.ObjectReleased
local WorldObject = {}

---UtilityBelt's property read: the key's enum picks the kind (a plain number is an IntId);
---`default` (UB's: 0, "" or false) when the object doesn't have it. No key: the pyreal value.
---@param key IntId|integer|RynthLua.EnumValue
---@param default? any
---@return any
function WorldObject:Value(key, default) end

---@param key IntId|integer|RynthLua.EnumValue
---@return boolean
function WorldObject:HasValue(key) end

---The outermost container or wielder holding it (itself when nothing does).
---@return RynthLua.WorldObject
function WorldObject:GetTopmostParent() end

---UB's id overload of DistanceTo2D.
---@param other RynthLua.ObjectRef
---@return number
function WorldObject:DistanceTo2d(other) end

---Queues SalvageAdd.
---@param options? RynthLua.ActionOptions|fun(action: RynthLua.ObjectAction)
---@param callback? fun(action: RynthLua.ObjectAction)|RynthLua.ActionOptions
---@return RynthLua.ObjectAction
function WorldObject:AddToSalvage(options, callback) end

---Queues TradeAdd.
---@param options? RynthLua.ActionOptions|fun(action: RynthLua.ObjectAction)
---@param callback? fun(action: RynthLua.ObjectAction)|RynthLua.ActionOptions
---@return RynthLua.ObjectAction
function WorldObject:AddToTrade(options, callback) end

---Queues RunAllOrdered({ SalvageAdd(this), Salvage() }).
---@param options? RynthLua.ActionOptions|fun(action: RynthLua.RunAllOrderedAction)
---@param callback? fun(action: RynthLua.RunAllOrderedAction)|RynthLua.ActionOptions
---@return RynthLua.RunAllOrderedAction
function WorldObject:Salvage(options, callback) end

---Queues Inscribe (fails: needs engine support).
---@param text string
---@return RynthLua.Action
function WorldObject:Inscribe(text, options, callback) end

---An int property (IntId), or `default` (0 when left out).
---@param id IntId|integer
---@param default? any
---@return integer
function WorldObject:IntValue(id, default) end

---An int64 property, or `default` (0).
---@param id integer
---@param default? any
---@return integer
function WorldObject:Int64Value(id, default) end

---A float property, or `default` (0).
---@param id integer
---@param default? any
---@return number
function WorldObject:FloatValue(id, default) end

---A string property (StringId), or `default` ("").
---@param id StringId|integer
---@param default? any
---@return string
function WorldObject:StringValue(id, default) end

---A bool property (BoolId), or `default` (false).
---@param id BoolId|integer
---@param default? any
---@return boolean
function WorldObject:BoolValue(id, default) end

---A data id property, or `default` (0).
---@param id integer
---@param default? any
---@return integer
function WorldObject:DataValue(id, default) end

---Metres to another object (3D); math.huge when a position is unknown.
---@param other RynthLua.ObjectRef
---@return number
function WorldObject:DistanceTo(other) end

---@param other RynthLua.ObjectRef
---@return number
function WorldObject:DistanceTo3D(other) end

---Metres on the ground plane (height ignored).
---@param other RynthLua.ObjectRef
---@return number
function WorldObject:DistanceTo2D(other) end

---Queues ObjectUse. Options and callback may come in either order.
---@param options? RynthLua.ActionOptions|fun(action: RynthLua.ObjectAction)
---@param callback? fun(action: RynthLua.ObjectAction)|RynthLua.ActionOptions
---@return RynthLua.ObjectAction
function WorldObject:Use(options, callback) end

---Queues ObjectUseOn (use this on `target`).
---@param target RynthLua.ObjectRef
---@param options? RynthLua.ActionOptions|fun(action: RynthLua.ObjectAction)
---@param callback? fun(action: RynthLua.ObjectAction)|RynthLua.ActionOptions
---@return RynthLua.ObjectAction
function WorldObject:UseOn(target, options, callback) end

---Queues ObjectGive.
---@param target RynthLua.ObjectRef
---@param options? RynthLua.ActionOptions|fun(action: RynthLua.ObjectAction)
---@param callback? fun(action: RynthLua.ObjectAction)|RynthLua.ActionOptions
---@return RynthLua.ObjectAction
function WorldObject:Give(target, options, callback) end

---Queues ObjectMove into `container`.
---@param container RynthLua.ObjectRef
---@param slot? integer
---@param stack? boolean   Merge into a matching stack there (default true).
---@param options? RynthLua.ActionOptions|fun(action: RynthLua.MoveAction)
---@param callback? fun(action: RynthLua.MoveAction)|RynthLua.ActionOptions
---@return RynthLua.MoveAction
---@overload fun(self: RynthLua.WorldObject, container: RynthLua.ObjectRef, options?: RynthLua.ActionOptions|fun(action: RynthLua.MoveAction), callback?: fun(action: RynthLua.MoveAction)|RynthLua.ActionOptions): RynthLua.MoveAction
function WorldObject:Move(container, slot, stack, options, callback) end

---Queues ObjectSplit: a new stack of `amount` (into `container`, default the same pack).
---@param amount integer
---@param container? RynthLua.ObjectRef
---@param slot? integer
---@param options? RynthLua.ActionOptions|fun(action: RynthLua.SplitAction)
---@param callback? fun(action: RynthLua.SplitAction)|RynthLua.ActionOptions
---@return RynthLua.SplitAction
---@overload fun(self: RynthLua.WorldObject, amount: integer, options?: RynthLua.ActionOptions|fun(action: RynthLua.SplitAction), callback?: fun(action: RynthLua.SplitAction)|RynthLua.ActionOptions): RynthLua.SplitAction
function WorldObject:Split(amount, container, slot, options, callback) end

---Queues ObjectDrop.
---@param options? RynthLua.ActionOptions|fun(action: RynthLua.ObjectAction)
---@param callback? fun(action: RynthLua.ObjectAction)|RynthLua.ActionOptions
---@return RynthLua.ObjectAction
function WorldObject:Drop(options, callback) end

---Queues ObjectSelect.
---@param options? RynthLua.ActionOptions|fun(action: RynthLua.ObjectAction)
---@param callback? fun(action: RynthLua.ObjectAction)|RynthLua.ActionOptions
---@return RynthLua.ObjectAction
function WorldObject:Select(options, callback) end

---Queues ObjectAppraise.
---@param options? RynthLua.ActionOptions|fun(action: RynthLua.ObjectAction)
---@param callback? fun(action: RynthLua.ObjectAction)|RynthLua.ActionOptions
---@return RynthLua.ObjectAction
function WorldObject:Appraise(options, callback) end

---Queues ObjectWield (the slot is an EquipMask; engine API v70+).
---@param slot? integer
---@param options? RynthLua.ActionOptions|fun(action: RynthLua.WieldAction)
---@param callback? fun(action: RynthLua.WieldAction)|RynthLua.ActionOptions
---@return RynthLua.WieldAction
---@overload fun(self: RynthLua.WorldObject, options?: RynthLua.ActionOptions|fun(action: RynthLua.WieldAction), callback?: fun(action: RynthLua.WieldAction)|RynthLua.ActionOptions): RynthLua.WieldAction
function WorldObject:Wield(slot, options, callback) end

------------------------------------------------------------------------------
-- game.World
------------------------------------------------------------------------------

---@class RynthLua.World
---@field Selected? RynthLua.WorldObject     The client's current selection.
---@field [integer] RynthLua.WorldObject?     game.World[id], like Get(id).
---@field OnObjectCreated RynthLua.Event.ObjectCreated
---@field OnObjectReleased RynthLua.Event.ObjectReleased
---@field OnChatText RynthLua.Event.ChatText
---@field OnTell RynthLua.Event.Tell
---@field OnSelected RynthLua.Event.Selected
---@field OnObjectSelected RynthLua.Event.Selected   Same event as OnSelected (UtilityBelt's name).
---@field OnLocalMessage RynthLua.Event.ChatText
---@field OnChannelMessage RynthLua.Event.ChatText
---@field OnFellowMessage RynthLua.Event.ChatText
---@field OnEmote RynthLua.Event.ChatText
---@field OnChatInput RynthLua.Event.ChatInput
---@field OnContainerOpened RynthLua.Event.Container
---@field OnContainerClosed RynthLua.Event.Container
---@field OnTick RynthLua.Event.Empty
---@field OpenContainer? RynthLua.WorldObject   The container on the ground you have open.
---@field Vendor RynthLua.Vendor
local World = {}

---@class RynthLua.VendorItem
---@field ObjectId integer
---@field Name string
---@field Amount integer        -1 = unlimited.
---@field UnitPrice integer
---@field StackSize integer
---@field MaxStackSize integer
---@field ObjectType RynthLua.EnumValue

---The open vendor (engine API v67+).
---@class RynthLua.Vendor
---@field IsOpen boolean
---@field VendorId integer
---@field Name string
---@field MinBuyValue integer
---@field MaxBuyValue integer
---@field Magic integer               1 when it deals in magic items.
---@field BuyPriceModifier number
---@field SellPriceModifier number
---@field CurrencyType integer        0 = pyreals, else the currency's WCID.
---@field CurrencyName string
---@field PlayerCurrencyAmount integer
---@field Category RynthLua.EnumValue  ObjectType flags of what it buys.
---@field Items RynthLua.VendorItem[]
---@field OnOpened RynthLua.Event.Empty
---@field OnClosed RynthLua.Event.Empty

---UtilityBelt's form: `local ok, wo = game.World.TryGet(id)`.
---@param id integer|string|RynthLua.WorldObject
---@return boolean ok
---@return RynthLua.WorldObject? wo
function World.TryGet(id) end

---The object, or nil when the client doesn't know it. Also takes "12345" / "0x80001234".
---@param id integer|string|RynthLua.WorldObject
---@return RynthLua.WorldObject?
function World.Get(id) end

---@param id integer|string|RynthLua.WorldObject
---@return boolean
function World.Exists(id) end

---Every known object matching the filter.
---@param filter? RynthLua.Filter
---@return RynthLua.WorldObject[]
function World.GetAll(filter) end

---Objects lying in the world (not you, not in a container, not wielded).
---@param filter? RynthLua.Filter
---@return RynthLua.WorldObject[]
function World.GetLandscape(filter) end

---Everything you carry: main pack items, side packs and their contents, and what you wear and wield.
---@param filter? RynthLua.Filter
---@return RynthLua.WorldObject[]
function World.GetInventory(filter) end

---The nearest landscape object matching the filter, or nil.
---@param filter RynthLua.Filter
---@param distanceType? DistanceType|integer   T3D (default) or T2D (ground plane).
---@return RynthLua.WorldObject?
function World.GetNearest(filter, distanceType) end

------------------------------------------------------------------------------
-- game.Character
------------------------------------------------------------------------------

---@class RynthLua.Vital
---@field Id VitalId|integer
---@field Type VitalId|integer   UB's name for the id.
---@field Name RynthLua.VitalName
---@field Current integer
---@field Max integer          Buffed maximum.
---@field Base integer         Unbuffed maximum.

---@class RynthLua.Vitals
---@field Health RynthLua.Vital
---@field Stamina RynthLua.Vital
---@field Mana RynthLua.Vital
---@field [integer] RynthLua.Vital   By VitalId.

---@class RynthLua.Skill
---@field Id SkillId|integer
---@field Type SkillId|integer   UB's name for the id.
---@field Name string
---@field Current integer      Buffed.
---@field Base integer
---@field Training SkillTrainingType|integer  0 Unusable, 1 Untrained, 2 Trained, 3 Specialized.
---@field TrainingName RynthLua.TrainingName
---@field IsTrained boolean    Trained or Specialized.

---Every skill, keyed by SkillId (pairs() lists them); names work too: Skills.WarMagic.
---@class RynthLua.Skills
---@field [integer] RynthLua.Skill
---@field [string] RynthLua.Skill

---@class RynthLua.Attribute
---@field Id AttributeId|integer
---@field Type AttributeId|integer   UB's name for the id.
---@field Name string
---@field Current integer      Buffed.
---@field Base integer

---@class RynthLua.Attributes
---@field Strength RynthLua.Attribute
---@field Endurance RynthLua.Attribute
---@field Quickness RynthLua.Attribute
---@field Coordination RynthLua.Attribute
---@field Focus RynthLua.Attribute
---@field Self RynthLua.Attribute
---@field [integer] RynthLua.Attribute

---@class RynthLua.Enchantment
---@field SpellId integer
---@field Name? string         Needs RynthAi.
---@field Permanent boolean
---@field Remaining? number    Seconds left (math.huge when permanent; nil without server time).
---@field ExpiresAt? number    Unix time (seconds) it runs out (math.huge when permanent).
---@field Duration? number     Full duration in seconds when known; -1 when permanent.

---@class RynthLua.SpellBook
---@field Count integer
---@field KnownSpellsIds integer[]   UB's property: every known spell id, sorted.
local SpellBook = {}
---@param spellId integer
---@return boolean
function SpellBook.IsKnown(spellId) end
---Every known spell id, sorted.
---@return integer[]
function SpellBook.KnownSpellIds() end
---The spell's name (needs RynthAi), or nil.
---@param spellId integer
---@return string?
function SpellBook.Name(spellId) end

---@class RynthLua.Character
---@field Id integer
---@field Name string
---@field Weenie? RynthLua.WorldObject     You, as a WorldObject.
---@field Level integer
---@field CombatMode CombatMode|integer
---@field CombatModeName RynthLua.CombatModeName
---@field InPortalSpace boolean
---@field Vitae number                     1.0 = no penalty, 0.95 = 5% vitae.
---@field Busy boolean                     Busy with a use or cast.
---@field Health integer
---@field Stamina integer
---@field Mana integer
---@field MaxHealth integer
---@field MaxStamina integer
---@field MaxMana integer
---@field BurdenUnits integer
---@field MaxBurdenUnits integer
---@field Burden number                    Percent of capacity (100 = full).
---@field TotalExperience integer
---@field UnassignedExperience integer
---@field AvailableLuminance integer
---@field MaximumLuminance integer
---@field Vitals RynthLua.Vitals
---@field Skills RynthLua.Skills
---@field Attributes RynthLua.Attributes
---@field SpellBook RynthLua.SpellBook
---@field OnVitalChanged RynthLua.Event.VitalChanged
---@field OnPortalSpaceEntered RynthLua.Event.Empty
---@field OnPortalSpaceExited RynthLua.Event.Empty
---@field OnDeath RynthLua.Event.Death
---@field OnCombatModeChanged RynthLua.Event.CombatModeChanged
---@field OnEnchantmentsChanged RynthLua.Event.EnchantmentsChanged
---@field OnLevelChanged RynthLua.Event.LevelChanged
---@field OnVitaeChanged RynthLua.Event.VitaeChanged
---@field OnTotalExperienceChanged RynthLua.Event.TotalExperienceChanged
---@field OnAvailableLuminanceChanged RynthLua.Event.LuminanceChanged
---@field Inventory RynthLua.WorldObject[]|fun(filter?: RynthLua.Filter): RynthLua.WorldObject[]   Everything you have (packs, their contents, equipment); call it to filter.
---@field Equipment RynthLua.WorldObject[]|fun(filter?: RynthLua.Filter): RynthLua.WorldObject[]   What you wear and wield.
---@field Containers RynthLua.WorldObject[]|fun(filter?: RynthLua.Filter): RynthLua.WorldObject[]  Your side packs.
---@field Trade RynthLua.Trade
local Character = {}

---The trade window (engine API v72+).
---@class RynthLua.Trade
---@field IsOpen boolean
---@field PartnerId integer
---@field YourItemIds integer[]
---@field PartnerItemIds integer[]
---@field YouAccepted boolean
---@field PartnerAccepted boolean
---@field OnStarted RynthLua.Event.Trade
---@field OnObjectAdded RynthLua.Event.Trade
---@field OnAccepted RynthLua.Event.Trade
---@field OnDeclined RynthLua.Event.Trade
---@field OnReset RynthLua.Event.Trade
---@field OnFailedToAddObject RynthLua.Event.Trade
---@field OnCompleted RynthLua.Event.Trade
---@field OnEnded RynthLua.Event.Trade
---@field OnFailed RynthLua.Event.Trade

---UB's search: the matching items.
---@param filter RynthLua.Filter
---@return RynthLua.WorldObject[]
function Character.GetInventory(filter) end

---UB's search: the first matching item, or nil.
---@param filter RynthLua.Filter
---@return RynthLua.WorldObject?
function Character.GetFirstInventory(filter) end

---UB's count: how many you have, counting stack sizes.
---@param filter RynthLua.Filter
---@return integer
function Character.GetInventoryCount(filter) end

---UB's name for Enchantments().
---@return RynthLua.Enchantment[]
function Character.ActiveEnchantments() end

---UB's name for Enchantments() (there's no inactive list here).
---@return RynthLua.Enchantment[]
function Character.AllEnchantments() end

---@param skill SkillId|integer|string   SkillId.WarMagic, 34 or "WarMagic"
---@return RynthLua.Skill
function Character.Skill(skill) end

---@param attribute AttributeId|integer|string
---@return RynthLua.Attribute
function Character.Attribute(attribute) end

---@param vital VitalId|integer|string
---@return RynthLua.Vital
function Character.Vital(vital) end

---@return RynthLua.Enchantment[]
function Character.Enchantments() end

---By spell id, or by spell name (names need RynthAi).
---@param spell integer|string
---@return boolean
function Character.HasEnchantment(spell) end

---@param spellId integer
---@return boolean
function Character.KnowsSpell(spellId) end

---Same as the Busy field.
---@return boolean
function Character.IsBusy() end

---How many you have, counting stack sizes.
---@param filter RynthLua.Filter
---@return integer
function Character.InventoryCount(filter) end

------------------------------------------------------------------------------
-- Actions
------------------------------------------------------------------------------

---@class RynthLua.ActionOptions
---@field Priority? integer|string       A number or an ActionType name ("Wield", "Inventory", ...).
---@field TimeoutMilliseconds? integer   Per attempt; 0 = no timeout.
---@field MaxRetryCount? integer         Retries after TooBusy / SpellFizzled.
---@field SkipChecks? boolean            Skip the pre-checks and the "client busy" wait.
---@field Name? string                   Shown in the queue and in Action.Name.

---A queued action. Its fields are rewritten as it runs; await(action) waits for IsFinished.
---@class RynthLua.Action
---@field ActionId integer
---@field ActionType ActionType|integer  UB's: the kind's priority category (ActionType.Inventory, ...).
---@field Kind string                    The game.Actions function that made it ("ObjectUse", ...).
---@field Name string
---@field ScriptName string
---@field Priority integer
---@field Options RynthLua.ActionOptions
---@field IsStarted boolean
---@field IsRunning boolean
---@field IsFinished boolean
---@field Success boolean
---@field Error ActionError|string       "None" while it hasn't failed.
---@field ErrorDetails string
---@field Result any                     What it produced (see each action), or nil.
---@field StartedAt? integer             clock() milliseconds.
---@field FinishedAt? integer            clock() milliseconds.
---@field CurrentRetryCount integer
local Action = {}
---Cancels it (Error = "Cancelled").
function Action.Cancel() end
---Same as await(action).
---@return RynthLua.Action
function Action.Await() end

---@class RynthLua.ObjectAction : RynthLua.Action
---@field ObjectId integer
---@field TargetId integer

---@class RynthLua.MoveAction : RynthLua.ObjectAction
---@field Slot integer
---@field Stack boolean

---@class RynthLua.SplitAction : RynthLua.ObjectAction
---@field NewStackSize integer
---@field Slot integer
---@field NewStackObjectId? integer   Set when it finished.

---@class RynthLua.WieldAction : RynthLua.ObjectAction
---@field Slot integer

---@class RynthLua.CastSpellAction : RynthLua.Action
---@field SpellId integer
---@field TargetId integer

---@class RynthLua.CombatModeAction : RynthLua.Action
---@field CombatMode CombatMode|integer
---@field CombatModeName RynthLua.CombatModeName

---@class RynthLua.SleepAction : RynthLua.Action
---@field Milliseconds integer

---@class RynthLua.InvokeChatAction : RynthLua.Action
---@field Text string

---@class RynthLua.RunAllOrderedAction : RynthLua.Action
---@field Actions RynthLua.Action[]

---Another script's action as game.ActionQueue shows it.
---@class RynthLua.ActionSummary
---@field Name string
---@field ActionType integer
---@field Kind string
---@field Priority integer
---@field ScriptName string
---@field IsStarted boolean
---@field IsRunning boolean
---@field IsFinished boolean

---Every call is Kind(args... [, options] [, callback]); options and callback may come in
---either order. Objects are WorldObjects or ids.
---@class RynthLua.Actions
local Actions = {}

---@param object RynthLua.ObjectRef
---@param target? RynthLua.ObjectRef     Use it on something (same as ObjectUseOn).
---@param options? RynthLua.ActionOptions|fun(action: RynthLua.ObjectAction)
---@param callback? fun(action: RynthLua.ObjectAction)|RynthLua.ActionOptions
---@return RynthLua.ObjectAction
---@overload fun(object: RynthLua.ObjectRef, options?: RynthLua.ActionOptions|fun(action: RynthLua.ObjectAction), callback?: fun(action: RynthLua.ObjectAction)|RynthLua.ActionOptions): RynthLua.ObjectAction
function Actions.ObjectUse(object, target, options, callback) end

---@param object RynthLua.ObjectRef
---@param target RynthLua.ObjectRef
---@param options? RynthLua.ActionOptions|fun(action: RynthLua.ObjectAction)
---@param callback? fun(action: RynthLua.ObjectAction)|RynthLua.ActionOptions
---@return RynthLua.ObjectAction
function Actions.ObjectUseOn(object, target, options, callback) end

---RynthLua's alias of ObjectUseOn (UB does use-on as ObjectUse(object, target)).
---@param object RynthLua.ObjectRef
---@param target RynthLua.ObjectRef
---@param options? RynthLua.ActionOptions|fun(action: RynthLua.ObjectAction)
---@param callback? fun(action: RynthLua.ObjectAction)|RynthLua.ActionOptions
---@return RynthLua.ObjectAction
function Actions.ObjectApply(object, target, options, callback) end

---@param object RynthLua.ObjectRef
---@param target RynthLua.ObjectRef
---@param options? RynthLua.ActionOptions|fun(action: RynthLua.ObjectAction)
---@param callback? fun(action: RynthLua.ObjectAction)|RynthLua.ActionOptions
---@return RynthLua.ObjectAction
function Actions.ObjectGive(object, target, options, callback) end

---Result: the object, once it is in the container (nil after a merge).
---@param object RynthLua.ObjectRef
---@param container RynthLua.ObjectRef
---@param slot? integer
---@param stack? boolean   Merge into a matching stack there (default true).
---@param options? RynthLua.ActionOptions|fun(action: RynthLua.MoveAction)
---@param callback? fun(action: RynthLua.MoveAction)|RynthLua.ActionOptions
---@return RynthLua.MoveAction
---@overload fun(object: RynthLua.ObjectRef, container: RynthLua.ObjectRef, options?: RynthLua.ActionOptions|fun(action: RynthLua.MoveAction), callback?: fun(action: RynthLua.MoveAction)|RynthLua.ActionOptions): RynthLua.MoveAction
function Actions.ObjectMove(object, container, slot, stack, options, callback) end

---UtilityBelt's form (object, container, newStackSize [, slot]) or the short form
---(object, newStackSize). Result: the new stack (nil if it couldn't be found).
---@param object RynthLua.ObjectRef
---@param container RynthLua.ObjectRef|integer   0 = the same pack.
---@param newStackSize integer
---@param slot? integer
---@param options? RynthLua.ActionOptions|fun(action: RynthLua.SplitAction)
---@param callback? fun(action: RynthLua.SplitAction)|RynthLua.ActionOptions
---@return RynthLua.SplitAction
---@overload fun(object: RynthLua.ObjectRef, newStackSize: integer, options?: RynthLua.ActionOptions|fun(action: RynthLua.SplitAction), callback?: fun(action: RynthLua.SplitAction)|RynthLua.ActionOptions): RynthLua.SplitAction
function Actions.ObjectSplit(object, container, newStackSize, slot, options, callback) end

---@param object RynthLua.ObjectRef
---@param options? RynthLua.ActionOptions|fun(action: RynthLua.ObjectAction)
---@param callback? fun(action: RynthLua.ObjectAction)|RynthLua.ActionOptions
---@return RynthLua.ObjectAction
function Actions.ObjectDrop(object, options, callback) end

---Result: the object. The slot is accepted but AC picks the item's default slot.
---@param object RynthLua.ObjectRef
---@param slot? integer
---@param options? RynthLua.ActionOptions|fun(action: RynthLua.WieldAction)
---@param callback? fun(action: RynthLua.WieldAction)|RynthLua.ActionOptions
---@return RynthLua.WieldAction
---@overload fun(object: RynthLua.ObjectRef, options?: RynthLua.ActionOptions|fun(action: RynthLua.WieldAction), callback?: fun(action: RynthLua.WieldAction)|RynthLua.ActionOptions): RynthLua.WieldAction
function Actions.ObjectWield(object, slot, options, callback) end

---Result: the object.
---@param object RynthLua.ObjectRef
---@param options? RynthLua.ActionOptions|fun(action: RynthLua.ObjectAction)
---@param callback? fun(action: RynthLua.ObjectAction)|RynthLua.ActionOptions
---@return RynthLua.ObjectAction
function Actions.ObjectSelect(object, options, callback) end

---Result: the object, with fresh appraisal data.
---@param object RynthLua.ObjectRef
---@param options? RynthLua.ActionOptions|fun(action: RynthLua.ObjectAction)
---@param callback? fun(action: RynthLua.ObjectAction)|RynthLua.ActionOptions
---@return RynthLua.ObjectAction
function Actions.ObjectAppraise(object, options, callback) end

---Needs magic mode. Fizzles and "too busy" are retried (MaxRetryCount).
---@param spellId integer
---@param target? RynthLua.ObjectRef   0 or nil: no target (self spells).
---@param options? RynthLua.ActionOptions|fun(action: RynthLua.CastSpellAction)
---@param callback? fun(action: RynthLua.CastSpellAction)|RynthLua.ActionOptions
---@return RynthLua.CastSpellAction
---@overload fun(spellId: integer, options?: RynthLua.ActionOptions|fun(action: RynthLua.CastSpellAction), callback?: fun(action: RynthLua.CastSpellAction)|RynthLua.ActionOptions): RynthLua.CastSpellAction
function Actions.CastSpell(spellId, target, options, callback) end

---@param mode CombatMode|integer|RynthLua.CombatModeName
---@param options? RynthLua.ActionOptions|fun(action: RynthLua.CombatModeAction)
---@param callback? fun(action: RynthLua.CombatModeAction)|RynthLua.ActionOptions
---@return RynthLua.CombatModeAction
function Actions.SetCombatMode(mode, options, callback) end

---As if typed in the chat bar (commands too). Runs at once, beside the queue. Result: true.
---@param text string
---@param options? RynthLua.ActionOptions|fun(action: RynthLua.InvokeChatAction)
---@param callback? fun(action: RynthLua.InvokeChatAction)|RynthLua.ActionOptions
---@return RynthLua.InvokeChatAction
function Actions.InvokeChat(text, options, callback) end

---Finishes after `milliseconds`. Runs beside the queue (handy inside RunAllOrdered).
---@param milliseconds number
---@param options? RynthLua.ActionOptions|fun(action: RynthLua.SleepAction)
---@param callback? fun(action: RynthLua.SleepAction)|RynthLua.ActionOptions
---@return RynthLua.SleepAction
function Actions.Sleep(milliseconds, options, callback) end

---Runs this script's actions one after another as one queue entry; stops at the first
---failure. Result: the list.
---@param actions RynthLua.Action[]
---@param options? RynthLua.ActionOptions|fun(action: RynthLua.RunAllOrderedAction)
---@param callback? fun(action: RynthLua.RunAllOrderedAction)|RynthLua.ActionOptions
---@return RynthLua.RunAllOrderedAction
function Actions.RunAllOrdered(actions, options, callback) end

-- UtilityBelt's other actions. Every one takes the same trailing [options] [, callback].

---Does nothing and succeeds.
---@return RynthLua.Action
function Actions.Blank(options, callback) end
---@param enabled boolean
---@return RynthLua.Action
function Actions.SetAutorun(enabled, options, callback) end
---A tell by object id ("/t Name, message").
---@param object RynthLua.ObjectRef
---@param message string
---@return RynthLua.Action
function Actions.SendTellById(object, message, options, callback) end
---Casts the spell of the wand, staff or orb you wield.
---@param target? RynthLua.ObjectRef
---@return RynthLua.Action
function Actions.CastEquippedWandSpell(target, options, callback) end
---Puts an item in the salvage panel (opening it with an Ust from your inventory).
---@param object RynthLua.ObjectRef
---@return RynthLua.ObjectAction
function Actions.SalvageAdd(object, options, callback) end
---Salvages what SalvageAdd put in the panel.
---@return RynthLua.Action
function Actions.Salvage(options, callback) end
---@param object RynthLua.ObjectRef   An item on the open vendor's list.
---@param amount? integer             Default 1.
---@return RynthLua.Action
function Actions.VendorAddToBuyList(object, amount, options, callback) end
---@param object RynthLua.ObjectRef   One of your items.
---@return RynthLua.Action
function Actions.VendorAddToSellList(object, options, callback) end
---@return RynthLua.Action
function Actions.VendorClearBuyList(options, callback) end
---@return RynthLua.Action
function Actions.VendorClearSellList(options, callback) end
---Sends the buy list as one transaction (engine API v67+).
---@return RynthLua.Action
function Actions.VendorBuyAll(options, callback) end
---Sends the sell list as one transaction.
---@return RynthLua.Action
function Actions.VendorSellAll(options, callback) end
---@param object RynthLua.ObjectRef
---@return RynthLua.ObjectAction
function Actions.TradeAdd(object, options, callback) end
---@return RynthLua.Action
function Actions.TradeAccept(options, callback) end
---@return RynthLua.Action
function Actions.TradeDecline(options, callback) end
---@return RynthLua.Action
function Actions.TradeReset(options, callback) end
---@return RynthLua.Action
function Actions.TradeEnd(options, callback) end
---Not available yet (fails: needs engine support).
function Actions.Login(characterId, options, callback) end
---Not available yet (fails: needs engine support).
function Actions.Logout(options, callback) end
---Not available yet (fails: needs engine support).
function Actions.SkillAdvance(skill, options, callback) end
---Not available yet (fails: needs engine support).
function Actions.AttributeAddExperience(attribute, experience, options, callback) end
---Not available yet (fails: needs engine support).
function Actions.SkillAddExperience(skill, experience, options, callback) end
---Not available yet (fails: needs engine support).
function Actions.VitalAddExperience(vital, experience, options, callback) end
---Not available yet (fails: needs engine support).
function Actions.FellowCreate(name, shareExperience, options, callback) end
---Not available yet (fails: needs engine support).
function Actions.FellowDisband(options, callback) end
---Not available yet (fails: needs engine support).
function Actions.FellowDismiss(object, options, callback) end
---Not available yet (fails: needs engine support).
function Actions.FellowSetLeader(object, options, callback) end
---Not available yet (fails: needs engine support).
function Actions.FellowQuit(disband, options, callback) end
---Not available yet (fails: needs engine support).
function Actions.FellowRecruit(object, options, callback) end
---Not available yet (fails: needs engine support).
function Actions.FellowSetOpen(open, options, callback) end
---Not available yet (fails: needs engine support).
function Actions.AllegianceSwear(object, options, callback) end
---Not available yet (fails: needs engine support).
function Actions.AllegianceBreak(options, callback) end
---Not available yet (fails: needs engine support).
function Actions.Inscribe(object, text, options, callback) end

---UB's options type: ActionOptions.new() (or ActionOptions()) gives an options table.
---@class RynthLua.ActionOptionsType
---@operator call: RynthLua.ActionOptions
ActionOptions = {}
---@return RynthLua.ActionOptions
function ActionOptions.new() end

---The plugin-wide action queue (all scripts).
---@class RynthLua.ActionQueue
---@field Queue (RynthLua.Action|RynthLua.ActionSummary)[]            Running and waiting, in run order.
---@field ImmediateQueue (RynthLua.Action|RynthLua.ActionSummary)[]   Sleep / InvokeChat.
---@field Count integer
---@field Current? RynthLua.Action|RynthLua.ActionSummary
---@field IsBusy boolean
local ActionQueue = {}
---True while the action is queued or running.
---@param action RynthLua.Action
---@return boolean
function ActionQueue.Contains(action) end
---Actions are queued when made: reports whether this one still is.
---@param action RynthLua.Action
---@return boolean
function ActionQueue.Add(action) end
---Cancels one of this script's actions. True when it was queued.
---@param action RynthLua.Action
---@return boolean
function ActionQueue.Remove(action) end
---Cancels all of this script's actions; returns how many.
---@return integer
function ActionQueue.Clear() end

------------------------------------------------------------------------------
-- Modules (require)
------------------------------------------------------------------------------

---require("json")
---@class RynthLua.JsonModule
local JsonModule = {}
---@param value any
---@param pretty? boolean
---@return string
function JsonModule.encode(value, pretty) end
---@param text string
---@return any
function JsonModule.decode(text) end
---@param value any
---@param pretty? boolean
---@return string
function JsonModule.Encode(value, pretty) end
---@param text string
---@return any
function JsonModule.Decode(text) end
---Like decode, but JSON null becomes json.null() (arrays keep their holes).
---@param text string
---@return any
function JsonModule.parse(text) end
---Same as encode.
---@param value any
---@return string
function JsonModule.serialize(value) end
---The null value (encodes as null).
---@return table
function JsonModule.null() end
---True for nil and json.null().
---@param value any
---@return boolean
function JsonModule.isnull(value) end

---@class RynthLua.Storage
local Storage = {}
---@param key string|number
---@param default? any
---@return any
function Storage.Get(key, default) end
---Any JSON-able value; nil removes the key.
---@param key string|number
---@param value any
function Storage.Set(key, value) end
---@param key string|number
---@return boolean removed
function Storage.Remove(key) end
---@param key string|number
---@return boolean
function Storage.Has(key) end
---@return string[]
function Storage.Keys() end
function Storage.Clear() end
---Writes now instead of within ~2 s. False when the write failed.
---@return boolean
function Storage.Save() end

---require("storage")
---@class RynthLua.StorageModule : RynthLua.Storage
---@field Character RynthLua.Storage   The same, for the logged-in character.

---@class RynthLua.FileAccess
---@field IsApproved boolean
---@field IsReadOnly boolean
local FileAccess = {}
---@param file string
---@return string? text
---@return string? error
function FileAccess.ReadText(file) end
---@param file string
---@return string[]? lines
---@return string? error
function FileAccess.ReadLines(file) end
---@param file string
---@param text string
---@return boolean ok
---@return string? error
function FileAccess.WriteText(file, text) end
---@param file string
---@param text string
---@return boolean ok
---@return string? error
function FileAccess.AppendText(file, text) end
---@param file string
---@return boolean exists
---@return string? error
function FileAccess.FileExists(file) end
---@param dir? string
---@return boolean exists
---@return string? error
function FileAccess.DirectoryExists(dir) end
---@param dir string
---@return boolean ok
---@return string? error
function FileAccess.CreateDirectory(dir) end
---@param file string
---@return boolean ok
---@return string? error
function FileAccess.DeleteFile(file) end
---Names relative to the folder.
---@param dir? string
---@return string[]? files
---@return string? error
function FileAccess.GetFiles(dir) end
---@param dir? string
---@return string[]? dirs
---@return string? error
function FileAccess.GetDirectories(dir) end

---require("filesystem")
---@class RynthLua.FileSystemModule
local FileSystemModule = {}
---The script's data folder (read and write).
---@return RynthLua.FileAccess
function FileSystemModule.GetData() end
---The script's own folder (read-only; folder scripts only).
---@return RynthLua.FileAccess
function FileSystemModule.GetScript() end
---Not available in RynthLua (raises an error).
function FileSystemModule.GetCustom() end

---require("rynth.ai")
---@class RynthLua.RynthAiModule
---@field Available boolean       RynthAi is loaded.
---@field IsLoaded boolean        Same as Available.
---@field MacroRunning boolean    Read, or assign true/false to start/stop the macro.
---@field CombatHook RynthLua.CombatHookInfo   The combat hook's state (read-only; a fresh table each read).
local RynthAiModule = {}
---A meta expression; numbers come back as numbers.
---@param expression string
---@return number|string
function RynthAiModule.Evaluate(expression) end
---Number, boolean or text; nil for an unknown setting.
---@param name string
---@return number|boolean|string|nil
function RynthAiModule.GetSetting(name) end
---False for an unknown setting. Saves the profile.
---@param name string
---@param value number|boolean|string
---@return boolean
function RynthAiModule.SetSetting(name, value) end
---@return string[]
function RynthAiModule.SettingNames() end
---Walk to a spot: numbers (S and W negative) or "33.5N" / "44.8W".
---@param ns number|string
---@param ew number|string
---@return boolean
function RynthAiModule.GoTo(ns, ew) end
---Stops navigation (and the macro).
function RynthAiModule.Stop() end
---The nearest object whose name contains `name`, and its distance.
---@param name string
---@param portalsOnly? boolean   Portals and NPCs only.
---@return RynthLua.WorldObject? object
---@return number? distance
function RynthAiModule.FindNearest(name, portalsOnly) end
---Metres, -1 when unknown. A name finds the nearest object by that name.
---@param target RynthLua.ObjectRef|string
---@return number
function RynthAiModule.DistanceTo(target) end
---Line of sight to the target.
---@param target RynthLua.ObjectRef|string
---@return boolean
function RynthAiModule.IsPathClear(target) end
---A chat line as if typed (works without RynthAi too).
---@param text string
function RynthAiModule.Command(text) end
---Same as Command.
---@param text string
function RynthAiModule.SubmitCommand(text) end
---This script chooses RynthAi's attack spells. RynthAi still picks the target, keeps the stance,
---the pacing and its debuffs, and casts what the handler answers. The handler runs as a script
---thread when RynthAi is about to cast (after its debuffs) and may yield, but RynthAi waits only
---until `ctx.deadline` (1 s by default) and then casts its own choice. It must not cast itself.
---Return `spellId, targetId [, flags]`, or nil to let RynthAi choose this time. targetId nil =
---ctx.targetId; it may also be another of `ctx.monsters` or the player. flags is "area" (or true),
---"nojudge", or a table `{ area = true, nojudge = true }`; anything else is an error. "area" marks
---a ring/wall/chain cast: no miss counting on the fight target, filed with the rings. "nojudge"
---marks a cast not meant to damage (a debuff, a drain): no miss counting (so no blacklist) and
---nothing on the Damage tab (no tier, element or fight cast count), but it still counts in the
---session attack-cast and casts-per-kill counters like any cast. A damaging cast at the fight
---target is tagged like RynthAi's own (wielded wand, element and level from the spell), and a
---single-target one takes part in RynthAi's kill-shot swap: a predicted kill moves RynthAi to
---another target while the projectile flies, and the next request names the new target. An
---unknown spell, a target that isn't the fight target, a scanned monster or the player, or a spell
---the server refused outright right after the script cast it (no components) is rejected and
---RynthAi casts its own; a rejected request is not a cast and appears in neither ctx.last nor ctx.pending. A refused spell stays parked
---for 600 s (as RynthAi parks a vital level); restocking components does not unpark it sooner.
---One script per kind: the first to register owns it; the same script registering again replaces
---its handler; nil clears it. An error declines that request; a handler still running 1 s past
---ctx.deadline is stopped and counts as an error; 3 errors in a row remove the handler.
---Stopping the script releases it. Keeps the script running while set. The deadlines (1 s attack,
---750 ms heal) and the lease are fixed: the RynthAi.Combat Claim's timeout parameters are reserved.
---While a script holds the attack kind, RynthAi treats the character as able to attack with magic
---even with War and Void untrained: a listed caster counts as the combat weapon when no usable
---listed melee/missile weapon exists.
---@param handler fun(ctx: RynthLua.CombatContext): (integer?, (integer|RynthLua.ObjectRef)?, ("area"|"nojudge"|boolean|{area: boolean?, nojudge: boolean?})?)|nil
---@return boolean ok
---@return string? why   "RynthAi has no combat hook", or "the attack handler is already set by <script>".
function RynthAiModule.SetAttackHandler(handler) end
---This script chooses the vitals heal: asked when health is under Heal At (in combat) or Top Off HP
---(idle), before kits, Heal Self and potions (the emergency kit / Stamina to Health step stays
---RynthAi's). Same contract as SetAttackHandler; ctx.healLine is the line, and ctx.targetId /
---ctx.targetHealth are informational: RynthAi's current fight target (0 when none) and its health.
---Return a heal and target 0/nil for YOURSELF (never the fight target), or e.g. a drain spell and
---a monster from `ctx.monsters` or ctx.targetId. A level the server had no components for is
---rejected for 600 s (RynthAi's own heal skips it as long; restocking does not unpark it sooner).
---The deadline is 750 ms; nil or no
---answer = kit, Heal Self, potion.
---@param handler fun(ctx: RynthLua.CombatContext): (integer?, (integer|RynthLua.ObjectRef)?, ("area"|"nojudge"|boolean|{area: boolean?, nojudge: boolean?})?)|nil
---@return boolean ok
---@return string? why
function RynthAiModule.SetHealHandler(handler) end
---Drops this script's attack and heal handlers (RynthAi chooses again at once).
function RynthAiModule.ClearCombatHandlers() end

---What a combat handler gets (one table per request).
---@class RynthLua.CombatContext
---@field seq integer            The request's id.
---@field kind "attack"|"heal"
---@field targetId integer       Attack: RynthAi's locked target. Heal: RynthAi's current fight target (informational; 0 when none).
---@field targetHealth number    That target's health 0..1; -1 unknown or no target.
---@field healthPct integer      Your health %, -1 unknown.
---@field staminaPct integer     Your stamina %, -1 unknown.
---@field manaPct integer        Your mana %, -1 unknown.
---@field healLine integer       Heal: the health line you are under (Heal At / Top Off HP). Attack: 0.
---@field inCombat boolean       A monster is engaged or within Monster Range.
---@field combatMode integer     1 NonCombat, 2 Melee, 4 Missile, 8 Magic.
---@field issuedAt number        clock() when RynthAi asked.
---@field deadline number        clock() after which RynthAi casts its own choice.
---@field monsters RynthLua.CombatMonster[]   RynthAi's targets in range (attackable, in sight), nearest first.
---@field last RynthLua.CombatLast             The most recent RESOLVED cast of this kind (its answer is in).
---@field pending RynthLua.CombatPending?      The cast of this kind still in flight (sent, no answer yet); nil = none.

---@class RynthLua.CombatMonster
---@field id integer
---@field distance number   Metres.

---A cast of this kind is in flight from its send until its answer: the UseDones answer the casts in flight of both kinds in send order (an attack sent first takes the first one, a heal sent after it the next),
---then resolved. ctx.last never changes after the fact: a cast moves from ctx.pending to ctx.last once,
---with its final err. Declines and rejects are not casts and appear in neither.
---With an older RynthAi (one that does not write these fields): ctx.last is the last decision RynthAi used (spell 0 =
---declined or rejected), with no err, and ctx.pending is always nil.
---@class RynthLua.CombatLast
---@field seq integer      The request seq of that cast; 0 before the first resolved cast.
---@field spell integer    The spell cast; 0 before the first resolved cast.
---@field target integer
---@field at number        clock() when it was sent.
---@field err integer?     Its answer, final: the UseDone error. 0 = done (it went off), 29 (0x1D) =
---                        refused 'too busy' (it never went off: not a miss), 1025 (0x401) = not enough
---                        mana, 1026 (0x402) = fizzled. nil only when no answer could be read: no UseDone
---                        within 7 s of the send, or another answer came first; also nil before the
---                        first resolved cast.

---@class RynthLua.CombatPending
---@field seq integer      The request seq of the cast in flight.
---@field spell integer
---@field target integer
---@field at number        clock() when it was sent.

---ai.CombatHook
---@class RynthLua.CombatHookInfo
---@field Supported boolean      RynthAi serves the combat hook now.
---@field AttackOwner string?    The script holding the attack kind.
---@field HealOwner string?      The script holding the heal kind.
---@field Claimed boolean        RynthAi granted RynthLua's last claim (false while another owner holds it).

---require("rynthlua")
---@class RynthLua.InfoModule
---@field Name string
---@field Version string
---@field ScriptName string
---@field ScriptFolder? string   nil for single-file scripts.
---@field DataFolder string

------------------------------------------------------------------------------
-- Enums (globals; also require("enums")), UtilityBelt's names and numbers. Most are plain
-- tables both ways: SkillId.MeleeDefense == 6 and SkillId[6] == "MeleeDefense". Names also
-- match case-insensitively (SkillId.meleedefense). Numbers have UB's enum methods at run
-- time: SkillId.Axe:ToNumber(), EquipMask.Head:HasFlags(...), AddFlags, RemoveFlags.
-- Int64Id, FloatId, StringId, BoolId, DataId, InstanceId and ObjectType values are typed
-- (RynthLua.EnumValue) so wo:Value(key) can tell them apart.
------------------------------------------------------------------------------

---Object classes (Decal / UtilityBelt numbering). `ObjectClass[5]` gives the name back ("Monster").
---@enum ObjectClass
ObjectClass = {
    Unknown = 0,
    MeleeWeapon = 1,
    Armor = 2,
    Clothing = 3,
    Jewelry = 4,
    Monster = 5,
    Food = 6,
    Money = 7,
    Misc = 8,
    MissileWeapon = 9,
    Container = 10,
    Gem = 11,
    SpellComponent = 12,
    Key = 13,
    Portal = 14,
    TradeNote = 15,
    ManaStone = 16,
    Plant = 17,
    BaseCooking = 18,
    BaseAlchemy = 19,
    BaseFletching = 20,
    CraftedCooking = 21,
    CraftedAlchemy = 22,
    CraftedFletching = 23,
    Player = 24,
    Vendor = 25,
    Door = 26,
    Corpse = 27,
    Lifestone = 28,
    HealingKit = 29,
    Lockpick = 30,
    WandStaffOrb = 31,
    Bundle = 32,
    Book = 33,
    Journal = 34,
    Sign = 35,
    Housing = 36,
    Npc = 37,
    Foci = 38,
    Salvage = 39,
    Ust = 40,
    Services = 41,
    Scroll = 42,
    NumObjectClasses = 43,   -- UB
}

---Skill ids (AC's numbering). `SkillId[6]` gives the name back.
---@enum SkillId
SkillId = {
    Undef = 0,
    ThrownWeapons = 12,       -- UB's spelling (also ThrownWeapon)
    ArmorRepair = 26,         -- UB's name (also ArmsAndArmorRepair)
    MissleWeapons = 47,       -- UB's spelling (also MissileWeapons)
    Axe = 1,
    Bow = 2,
    Crossbow = 3,
    Dagger = 4,
    Mace = 5,
    MeleeDefense = 6,
    MissileDefense = 7,
    Sling = 8,
    Spear = 9,
    Staff = 10,
    Sword = 11,
    ThrownWeapon = 12,
    UnarmedCombat = 13,
    ArcaneLore = 14,
    MagicDefense = 15,
    ManaConversion = 16,
    Spellcraft = 17,
    ItemTinkering = 18,
    AssessPerson = 19,
    Deception = 20,
    Healing = 21,
    Jump = 22,
    Lockpick = 23,
    Run = 24,
    Awareness = 25,
    ArmsAndArmorRepair = 26,
    AssessCreature = 27,
    WeaponTinkering = 28,
    ArmorTinkering = 29,
    MagicItemTinkering = 30,
    CreatureEnchantment = 31,
    ItemEnchantment = 32,
    LifeMagic = 33,
    WarMagic = 34,
    Leadership = 35,
    Loyalty = 36,
    Fletching = 37,
    Alchemy = 38,
    Cooking = 39,
    Salvaging = 40,
    TwoHandedCombat = 41,
    Gearcraft = 42,
    VoidMagic = 43,
    HeavyWeapons = 44,
    LightWeapons = 45,
    FinesseWeapons = 46,
    MissileWeapons = 47,
    Shield = 48,
    DualWield = 49,
    Recklessness = 50,
    SneakAttack = 51,
    DirtyFighting = 52,
    Challenge = 53,
    Summoning = 54,
}

---Attribute ids (AC's numbering).
---@enum AttributeId
AttributeId = {
    Undef = 0,
    Strength = 1,
    Endurance = 2,
    Quickness = 3,
    Coordination = 4,
    Focus = 5,
    Self = 6,
}

---Vital ids (UtilityBelt's numbering): Health 1, Stamina 3, Mana 5.
---@enum VitalId
VitalId = {
    Health = 1,
    Stamina = 3,
    Mana = 5,
}

---Combat modes (AC's values).
---@enum CombatMode
CombatMode = {
    NonCombat = 1,
    Melee = 2,
    Missile = 4,
    Magic = 8,
    Invalid = 153,
}

---Chat message types (UtilityBelt's ChatMessageType names). `World.OnChatText` gives them as `e.Type`.
---@enum ChatType
ChatType = {
    Default = 0,
    Speech = 2,
    Tell = 3,
    OutgoingTell = 4,
    System = 5,
    Combat = 6,
    Magic = 7,
    Channels = 8,
    OutgoingChannel = 9,
    Social = 10,
    OutgoingSocial = 11,
    Emote = 12,
    Advancement = 13,
    Abuse = 14,
    Help = 15,
    Appraisal = 16,
    Spellcasting = 17,
    Allegiance = 18,
    Fellowship = 19,
    WorldBroadcast = 20,
    CombatEnemy = 21,
    CombatSelf = 22,
    Recall = 23,
    Craft = 24,
    Salvaging = 25,
    AdminTell = 31,
}

---Int property ids (ACE's PropertyInt names) for `wo:IntValue(id)`. `IntId.ObjectType` is UtilityBelt's name for ItemType (1).
---@enum IntId
IntId = {
    Undef = 0,
    ItemType = 1,
    CreatureType = 2,
    PaletteTemplate = 3,
    ClothingPriority = 4,
    EncumbranceVal = 5,
    ItemsCapacity = 6,
    ContainersCapacity = 7,
    Mass = 8,
    ValidLocations = 9,
    CurrentWieldedLocation = 10,
    MaxStackSize = 11,
    StackSize = 12,
    StackUnitEncumbrance = 13,
    StackUnitMass = 14,
    StackUnitValue = 15,
    ItemUseable = 16,
    RareId = 17,
    UiEffects = 18,
    Value = 19,
    CoinValue = 20,
    TotalExperience = 21,
    AvailableCharacter = 22,
    TotalSkillCredits = 23,
    AvailableSkillCredits = 24,
    Level = 25,
    AccountRequirements = 26,
    ArmorType = 27,
    ArmorLevel = 28,
    AllegianceCpPool = 29,
    AllegianceRank = 30,
    ChannelsAllowed = 31,
    ChannelsActive = 32,
    Bonded = 33,
    MonarchsRank = 34,
    AllegianceFollowers = 35,
    ResistMagic = 36,
    ResistItemAppraisal = 37,
    ResistLockpick = 38,
    DeprecatedResistRepair = 39,
    CombatMode = 40,
    CurrentAttackHeight = 41,
    CombatCollisions = 42,
    NumDeaths = 43,
    Damage = 44,
    DamageType = 45,
    DefaultCombatStyle = 46,
    AttackType = 47,
    WeaponSkill = 48,
    WeaponTime = 49,
    AmmoType = 50,
    CombatUse = 51,
    ParentLocation = 52,
    PlacementPosition = 53,
    WeaponEncumbrance = 54,
    WeaponMass = 55,
    ShieldValue = 56,
    ShieldEncumbrance = 57,
    MissileInventoryLocation = 58,
    FullDamageType = 59,
    WeaponRange = 60,
    AttackersSkill = 61,
    DefendersSkill = 62,
    AttackersSkillValue = 63,
    AttackersClass = 64,
    Placement = 65,
    CheckpointStatus = 66,
    Tolerance = 67,
    TargetingTactic = 68,
    CombatTactic = 69,
    HomesickTargetingTactic = 70,
    NumFollowFailures = 71,
    FriendType = 72,
    FoeType = 73,
    MerchandiseItemTypes = 74,
    MerchandiseMinValue = 75,
    MerchandiseMaxValue = 76,
    NumItemsSold = 77,
    NumItemsBought = 78,
    MoneyIncome = 79,
    MoneyOutflow = 80,
    MaxGeneratedObjects = 81,
    InitGeneratedObjects = 82,
    ActivationResponse = 83,
    OriginalValue = 84,
    NumMoveFailures = 85,
    MinLevel = 86,
    MaxLevel = 87,
    LockpickMod = 88,
    BoosterEnum = 89,
    BoostValue = 90,
    MaxStructure = 91,
    Structure = 92,
    PhysicsState = 93,
    TargetType = 94,
    RadarBlipColor = 95,
    EncumbranceCapacity = 96,
    LoginTimestamp = 97,
    CreationTimestamp = 98,
    PkLevelModifier = 99,
    GeneratorType = 100,
    AiAllowedCombatStyle = 101,
    LogoffTimestamp = 102,
    GeneratorDestructionType = 103,
    ActivationCreateClass = 104,
    ItemWorkmanship = 105,
    ItemSpellcraft = 106,
    ItemCurMana = 107,
    ItemMaxMana = 108,
    ItemDifficulty = 109,
    ItemAllegianceRankLimit = 110,
    PortalBitmask = 111,
    AdvocateLevel = 112,
    Gender = 113,
    Attuned = 114,
    ItemSkillLevelLimit = 115,
    GateLogic = 116,
    ItemManaCost = 117,
    Logoff = 118,
    Active = 119,
    AttackHeight = 120,
    NumAttackFailures = 121,
    AiCpThreshold = 122,
    AiAdvancementStrategy = 123,
    Version = 124,
    Age = 125,
    VendorHappyMean = 126,
    VendorHappyVariance = 127,
    CloakStatus = 128,
    VitaeCpPool = 129,
    NumServicesSold = 130,
    MaterialType = 131,
    NumAllegianceBreaks = 132,
    ShowableOnRadar = 133,
    PlayerKillerStatus = 134,
    VendorHappyMaxItems = 135,
    ScorePageNum = 136,
    ScoreConfigNum = 137,
    ScoreNumScores = 138,
    DeathLevel = 139,
    AiOptions = 140,
    OpenToEveryone = 141,
    GeneratorTimeType = 142,
    GeneratorStartTime = 143,
    GeneratorEndTime = 144,
    GeneratorEndDestructionType = 145,
    XpOverride = 146,
    NumCrashAndTurns = 147,
    ComponentWarningThreshold = 148,
    HouseStatus = 149,
    HookPlacement = 150,
    HookType = 151,
    HookItemType = 152,
    AiPpThreshold = 153,
    GeneratorVersion = 154,
    HouseType = 155,
    PickupEmoteOffset = 156,
    WeenieIteration = 157,
    WieldRequirements = 158,
    WieldSkillType = 159,
    WieldDifficulty = 160,
    HouseMaxHooksUsable = 161,
    HouseCurrentHooksUsable = 162,
    AllegianceMinLevel = 163,
    AllegianceMaxLevel = 164,
    HouseRelinkHookCount = 165,
    SlayerCreatureType = 166,
    ConfirmationInProgress = 167,
    ConfirmationTypeInProgress = 168,
    TsysMutationData = 169,
    NumItemsInMaterial = 170,
    NumTimesTinkered = 171,
    AppraisalLongDescDecoration = 172,
    AppraisalLockpickSuccessPercent = 173,
    AppraisalPages = 174,
    AppraisalMaxPages = 175,
    AppraisalItemSkill = 176,
    GemCount = 177,
    GemType = 178,
    ImbuedEffect = 179,
    AttackersRawSkillValue = 180,
    ChessRank = 181,
    ChessTotalGames = 182,
    ChessGamesWon = 183,
    ChessGamesLost = 184,
    TypeOfAlteration = 185,
    SkillToBeAltered = 186,
    SkillAlterationCount = 187,
    HeritageGroup = 188,
    TransferFromAttribute = 189,
    TransferToAttribute = 190,
    AttributeTransferCount = 191,
    FakeFishingSkill = 192,
    NumKeys = 193,
    DeathTimestamp = 194,
    PkTimestamp = 195,
    VictimTimestamp = 196,
    HookGroup = 197,
    AllegianceSwearTimestamp = 198,
    HousePurchaseTimestamp = 199,
    RedirectableEquippedArmorCount = 200,
    MeleeDefenseImbuedEffectTypeCache = 201,
    MissileDefenseImbuedEffectTypeCache = 202,
    MagicDefenseImbuedEffectTypeCache = 203,
    ElementalDamageBonus = 204,
    ImbueAttempts = 205,
    ImbueSuccesses = 206,
    CreatureKills = 207,
    PlayerKillsPk = 208,
    PlayerKillsPkl = 209,
    RaresTierOne = 210,
    RaresTierTwo = 211,
    RaresTierThree = 212,
    RaresTierFour = 213,
    RaresTierFive = 214,
    AugmentationStat = 215,
    AugmentationFamilyStat = 216,
    AugmentationInnateFamily = 217,
    AugmentationInnateStrength = 218,
    AugmentationInnateEndurance = 219,
    AugmentationInnateCoordination = 220,
    AugmentationInnateQuickness = 221,
    AugmentationInnateFocus = 222,
    AugmentationInnateSelf = 223,
    AugmentationSpecializeSalvaging = 224,
    AugmentationSpecializeItemTinkering = 225,
    AugmentationSpecializeArmorTinkering = 226,
    AugmentationSpecializeMagicItemTinkering = 227,
    AugmentationSpecializeWeaponTinkering = 228,
    AugmentationExtraPackSlot = 229,
    AugmentationIncreasedCarryingCapacity = 230,
    AugmentationLessDeathItemLoss = 231,
    AugmentationSpellsRemainPastDeath = 232,
    AugmentationCriticalDefense = 233,
    AugmentationBonusXp = 234,
    AugmentationBonusSalvage = 235,
    AugmentationBonusImbueChance = 236,
    AugmentationFasterRegen = 237,
    AugmentationIncreasedSpellDuration = 238,
    AugmentationResistanceFamily = 239,
    AugmentationResistanceSlash = 240,
    AugmentationResistancePierce = 241,
    AugmentationResistanceBlunt = 242,
    AugmentationResistanceAcid = 243,
    AugmentationResistanceFire = 244,
    AugmentationResistanceFrost = 245,
    AugmentationResistanceLightning = 246,
    RaresTierOneLogin = 247,
    RaresTierTwoLogin = 248,
    RaresTierThreeLogin = 249,
    RaresTierFourLogin = 250,
    RaresTierFiveLogin = 251,
    RaresLoginTimestamp = 252,
    RaresTierSix = 253,
    RaresTierSeven = 254,
    RaresTierSixLogin = 255,
    RaresTierSevenLogin = 256,
    ItemAttributeLimit = 257,
    ItemAttributeLevelLimit = 258,
    ItemAttribute2ndLimit = 259,
    ItemAttribute2ndLevelLimit = 260,
    CharacterTitleId = 261,
    NumCharacterTitles = 262,
    ResistanceModifierType = 263,
    FreeTinkersBitfield = 264,
    EquipmentSetId = 265,
    PetClass = 266,
    Lifespan = 267,
    RemainingLifespan = 268,
    UseCreateQuantity = 269,
    WieldRequirements2 = 270,
    WieldSkillType2 = 271,
    WieldDifficulty2 = 272,
    WieldRequirements3 = 273,
    WieldSkillType3 = 274,
    WieldDifficulty3 = 275,
    WieldRequirements4 = 276,
    WieldSkillType4 = 277,
    WieldDifficulty4 = 278,
    Unique = 279,
    SharedCooldown = 280,
    Faction1Bits = 281,
    Faction2Bits = 282,
    Faction3Bits = 283,
    Hatred1Bits = 284,
    Hatred2Bits = 285,
    Hatred3Bits = 286,
    SocietyRankCelhan = 287,
    SocietyRankEldweb = 288,
    SocietyRankRadblo = 289,
    HearLocalSignals = 290,
    HearLocalSignalsRadius = 291,
    Cleaving = 292,
    AugmentationSpecializeGearcraft = 293,
    AugmentationInfusedCreatureMagic = 294,
    AugmentationInfusedItemMagic = 295,
    AugmentationInfusedLifeMagic = 296,
    AugmentationInfusedWarMagic = 297,
    AugmentationCriticalExpertise = 298,
    AugmentationCriticalPower = 299,
    AugmentationSkilledMelee = 300,
    AugmentationSkilledMissile = 301,
    AugmentationSkilledMagic = 302,
    ImbuedEffect2 = 303,
    ImbuedEffect3 = 304,
    ImbuedEffect4 = 305,
    ImbuedEffect5 = 306,
    DamageRating = 307,
    DamageResistRating = 308,
    AugmentationDamageBonus = 309,
    AugmentationDamageReduction = 310,
    ImbueStackingBits = 311,
    HealOverTime = 312,
    CritRating = 313,
    CritDamageRating = 314,
    CritResistRating = 315,
    CritDamageResistRating = 316,
    HealingResistRating = 317,
    DamageOverTime = 318,
    ItemMaxLevel = 319,
    ItemXpStyle = 320,
    EquipmentSetExtra = 321,
    AetheriaBitfield = 322,
    HealingBoostRating = 323,
    HeritageSpecificArmor = 324,
    AlternateRacialSkills = 325,
    AugmentationJackOfAllTrades = 326,
    AugmentationResistanceNether = 327,
    AugmentationInfusedVoidMagic = 328,
    WeaknessRating = 329,
    NetherOverTime = 330,
    NetherResistRating = 331,
    LuminanceAward = 332,
    LumAugDamageRating = 333,
    LumAugDamageReductionRating = 334,
    LumAugCritDamageRating = 335,
    LumAugCritReductionRating = 336,
    LumAugSurgeEffectRating = 337,
    LumAugSurgeChanceRating = 338,
    LumAugItemManaUsage = 339,
    LumAugItemManaGain = 340,
    LumAugVitality = 341,
    LumAugHealingRating = 342,
    LumAugSkilledCraft = 343,
    LumAugSkilledSpec = 344,
    LumAugNoDestroyCraft = 345,
    RestrictInteraction = 346,
    OlthoiLootTimestamp = 347,
    OlthoiLootStep = 348,
    UseCreatesContractId = 349,
    DotResistRating = 350,
    LifeResistRating = 351,
    CloakWeaveProc = 352,
    WeaponType = 353,
    MeleeMastery = 354,
    RangedMastery = 355,
    SneakAttackRating = 356,
    RecklessnessRating = 357,
    DeceptionRating = 358,
    CombatPetRange = 359,
    WeaponAuraDamage = 360,
    WeaponAuraSpeed = 361,
    SummoningMastery = 362,
    HeartbeatLifespan = 363,
    UseLevelRequirement = 364,
    LumAugAllSkills = 365,
    UseRequiresSkill = 366,
    UseRequiresSkillLevel = 367,
    UseRequiresSkillSpec = 368,
    UseRequiresLevel = 369,
    GearDamage = 370,
    GearDamageResist = 371,
    GearCrit = 372,
    GearCritResist = 373,
    GearCritDamage = 374,
    GearCritDamageResist = 375,
    GearHealingBoost = 376,
    GearNetherResist = 377,
    GearLifeResist = 378,
    GearMaxHealth = 379,
    Unknown380 = 380,
    PKDamageRating = 381,
    PKDamageResistRating = 382,
    GearPKDamageRating = 383,
    GearPKDamageResistRating = 384,
    Unknown385 = 385,
    Overpower = 386,
    OverpowerResist = 387,
    GearOverpower = 388,
    GearOverpowerResist = 389,
    Enlightenment = 390,
    PCAPRecordedAutonomousMovement = 8007,
    PCAPRecordedMaxVelocityEstimated = 8030,
    PCAPRecordedPlacement = 8041,
    PCAPRecordedAppraisalPages = 8042,
    PCAPRecordedAppraisalMaxPages = 8043,
    TotalLogins = 9001,
    DeletionTimestamp = 9002,
    CharacterOptions1 = 9003,
    CharacterOptions2 = 9004,
    LootTier = 9005,
    GeneratorProbability = 9006,
    CurrentLoyaltyAtLastLogoff = 9008,
    CurrentLeadershipAtLastLogoff = 9009,
    AllegianceOfficerRank = 9010,
    HouseRentTimestamp = 9011,
    Hairstyle = 9012,
    VisualClothingPriority = 9013,
    SquelchGlobal = 9014,
    InventoryOrder = 9015,
    MerchandiseObjectTypes = 74,   -- UB's name (MerchandiseItemTypes)
    HookObjectType = 152,          -- UB's name (HookItemType)
    ObjectType = 1,
}

---Bool property ids (ACE names), typed: wo:Value(BoolId.Attackable).
---@class RynthLua.Enum.BoolId
---@field Undef RynthLua.EnumValue  0
---@field Stuck RynthLua.EnumValue  1
---@field Open RynthLua.EnumValue  2
---@field Locked RynthLua.EnumValue  3
---@field RotProof RynthLua.EnumValue  4
---@field AllegianceUpdateRequest RynthLua.EnumValue  5
---@field AiUsesMana RynthLua.EnumValue  6
---@field AiUseHumanMagicAnimations RynthLua.EnumValue  7
---@field AllowGive RynthLua.EnumValue  8
---@field CurrentlyAttacking RynthLua.EnumValue  9
---@field AttackerAi RynthLua.EnumValue  10
---@field IgnoreCollisions RynthLua.EnumValue  11
---@field ReportCollisions RynthLua.EnumValue  12
---@field Ethereal RynthLua.EnumValue  13
---@field GravityStatus RynthLua.EnumValue  14
---@field LightsStatus RynthLua.EnumValue  15
---@field ScriptedCollision RynthLua.EnumValue  16
---@field Inelastic RynthLua.EnumValue  17
---@field Visibility RynthLua.EnumValue  18
---@field Attackable RynthLua.EnumValue  19
---@field SafeSpellComponents RynthLua.EnumValue  20
---@field AdvocateState RynthLua.EnumValue  21
---@field Inscribable RynthLua.EnumValue  22
---@field DestroyOnSell RynthLua.EnumValue  23
---@field UiHidden RynthLua.EnumValue  24
---@field IgnoreHouseBarriers RynthLua.EnumValue  25
---@field HiddenAdmin RynthLua.EnumValue  26
---@field PkWounder RynthLua.EnumValue  27
---@field PkKiller RynthLua.EnumValue  28
---@field NoCorpse RynthLua.EnumValue  29
---@field UnderLifestoneProtection RynthLua.EnumValue  30
---@field ItemManaUpdatePending RynthLua.EnumValue  31
---@field GeneratorStatus RynthLua.EnumValue  32
---@field ResetMessagePending RynthLua.EnumValue  33
---@field DefaultOpen RynthLua.EnumValue  34
---@field DefaultLocked RynthLua.EnumValue  35
---@field DefaultOn RynthLua.EnumValue  36
---@field OpenForBusiness RynthLua.EnumValue  37
---@field IsFrozen RynthLua.EnumValue  38
---@field DealMagicalItems RynthLua.EnumValue  39
---@field LogoffImDead RynthLua.EnumValue  40
---@field ReportCollisionsAsEnvironment RynthLua.EnumValue  41
---@field AllowEdgeSlide RynthLua.EnumValue  42
---@field AdvocateQuest RynthLua.EnumValue  43
---@field IsAdmin RynthLua.EnumValue  44
---@field IsArch RynthLua.EnumValue  45
---@field IsSentinel RynthLua.EnumValue  46
---@field IsAdvocate RynthLua.EnumValue  47
---@field CurrentlyPoweringUp RynthLua.EnumValue  48
---@field GeneratorEnteredWorld RynthLua.EnumValue  49
---@field NeverFailCasting RynthLua.EnumValue  50
---@field VendorService RynthLua.EnumValue  51
---@field AiImmobile RynthLua.EnumValue  52
---@field DamagedByCollisions RynthLua.EnumValue  53
---@field IsDynamic RynthLua.EnumValue  54
---@field IsHot RynthLua.EnumValue  55
---@field IsAffecting RynthLua.EnumValue  56
---@field AffectsAis RynthLua.EnumValue  57
---@field SpellQueueActive RynthLua.EnumValue  58
---@field GeneratorDisabled RynthLua.EnumValue  59
---@field IsAcceptingTells RynthLua.EnumValue  60
---@field LoggingChannel RynthLua.EnumValue  61
---@field OpensAnyLock RynthLua.EnumValue  62
---@field UnlimitedUse RynthLua.EnumValue  63
---@field GeneratedTreasureItem RynthLua.EnumValue  64
---@field IgnoreMagicResist RynthLua.EnumValue  65
---@field IgnoreMagicArmor RynthLua.EnumValue  66
---@field AiAllowTrade RynthLua.EnumValue  67
---@field SpellComponentsRequired RynthLua.EnumValue  68
---@field IsSellable RynthLua.EnumValue  69
---@field IgnoreShieldsBySkill RynthLua.EnumValue  70
---@field NoDraw RynthLua.EnumValue  71
---@field ActivationUntargeted RynthLua.EnumValue  72
---@field HouseHasGottenPriorityBootPos RynthLua.EnumValue  73
---@field GeneratorAutomaticDestruction RynthLua.EnumValue  74
---@field HouseHooksVisible RynthLua.EnumValue  75
---@field HouseRequiresMonarch RynthLua.EnumValue  76
---@field HouseHooksEnabled RynthLua.EnumValue  77
---@field HouseNotifiedHudOfHookCount RynthLua.EnumValue  78
---@field AiAcceptEverything RynthLua.EnumValue  79
---@field IgnorePortalRestrictions RynthLua.EnumValue  80
---@field RequiresBackpackSlot RynthLua.EnumValue  81
---@field DontTurnOrMoveWhenGiving RynthLua.EnumValue  82
---@field NpcLooksLikeObject RynthLua.EnumValue  83
---@field IgnoreCloIcons RynthLua.EnumValue  84
---@field AppraisalHasAllowedWielder RynthLua.EnumValue  85
---@field ChestRegenOnClose RynthLua.EnumValue  86
---@field LogoffInMinigame RynthLua.EnumValue  87
---@field PortalShowDestination RynthLua.EnumValue  88
---@field PortalIgnoresPkAttackTimer RynthLua.EnumValue  89
---@field NpcInteractsSilently RynthLua.EnumValue  90
---@field Retained RynthLua.EnumValue  91
---@field IgnoreAuthor RynthLua.EnumValue  92
---@field Limbo RynthLua.EnumValue  93
---@field AppraisalHasAllowedActivator RynthLua.EnumValue  94
---@field ExistedBeforeAllegianceXpChanges RynthLua.EnumValue  95
---@field IsDeaf RynthLua.EnumValue  96
---@field IsPsr RynthLua.EnumValue  97
---@field Invincible RynthLua.EnumValue  98
---@field Ivoryable RynthLua.EnumValue  99
---@field Dyable RynthLua.EnumValue  100
---@field CanGenerateRare RynthLua.EnumValue  101
---@field CorpseGeneratedRare RynthLua.EnumValue  102
---@field NonProjectileMagicImmune RynthLua.EnumValue  103
---@field ActdReceivedItems RynthLua.EnumValue  104
---@field Unknown105 RynthLua.EnumValue  105
---@field FirstEnterWorldDone RynthLua.EnumValue  106
---@field RecallsDisabled RynthLua.EnumValue  107
---@field RareUsesTimer RynthLua.EnumValue  108
---@field ActdPreorderReceivedItems RynthLua.EnumValue  109
---@field Afk RynthLua.EnumValue  110
---@field IsGagged RynthLua.EnumValue  111
---@field ProcSpellSelfTargeted RynthLua.EnumValue  112
---@field IsAllegianceGagged RynthLua.EnumValue  113
---@field EquipmentSetTriggerPiece RynthLua.EnumValue  114
---@field Uninscribe RynthLua.EnumValue  115
---@field WieldOnUse RynthLua.EnumValue  116
---@field ChestClearedWhenClosed RynthLua.EnumValue  117
---@field NeverAttack RynthLua.EnumValue  118
---@field SuppressGenerateEffect RynthLua.EnumValue  119
---@field TreasureCorpse RynthLua.EnumValue  120
---@field EquipmentSetAddLevel RynthLua.EnumValue  121
---@field BarberActive RynthLua.EnumValue  122
---@field TopLayerPriority RynthLua.EnumValue  123
---@field NoHeldItemShown RynthLua.EnumValue  124
---@field LoginAtLifestone RynthLua.EnumValue  125
---@field OlthoiPk RynthLua.EnumValue  126
---@field Account15Days RynthLua.EnumValue  127
---@field HadNoVitae RynthLua.EnumValue  128
---@field NoOlthoiTalk RynthLua.EnumValue  129
---@field AutowieldLeft RynthLua.EnumValue  130
---@field LinkedPortalOneSummon RynthLua.EnumValue  9001
---@field LinkedPortalTwoSummon RynthLua.EnumValue  9002
---@field HouseEvicted RynthLua.EnumValue  9003
---@field UntrainedSkills RynthLua.EnumValue  9004
---@field IsEnvoy RynthLua.EnumValue  9005
---@field UnspecializedSkills RynthLua.EnumValue  9006
---@field FreeSkillResetRenewed RynthLua.EnumValue  9007
---@field FreeAttributeResetRenewed RynthLua.EnumValue  9008
---@field SkillTemplesTimerReset RynthLua.EnumValue  9009
---@field FreeMasteryResetRenewed RynthLua.EnumValue  9010
---@type RynthLua.Enum.BoolId
BoolId = {}

---String property ids (ACE names), typed: wo:Value(StringId.Name). tostring gives the name.
---@class RynthLua.Enum.StringId
---@field Undef RynthLua.EnumValue  0
---@field Name RynthLua.EnumValue  1
---@field Title RynthLua.EnumValue  2
---@field Sex RynthLua.EnumValue  3
---@field HeritageGroup RynthLua.EnumValue  4
---@field Template RynthLua.EnumValue  5
---@field AttackersName RynthLua.EnumValue  6
---@field Inscription RynthLua.EnumValue  7
---@field ScribeName RynthLua.EnumValue  8
---@field VendorsName RynthLua.EnumValue  9
---@field Fellowship RynthLua.EnumValue  10
---@field MonarchsName RynthLua.EnumValue  11
---@field LockCode RynthLua.EnumValue  12
---@field KeyCode RynthLua.EnumValue  13
---@field Use RynthLua.EnumValue  14
---@field ShortDesc RynthLua.EnumValue  15
---@field LongDesc RynthLua.EnumValue  16
---@field ActivationTalk RynthLua.EnumValue  17
---@field UseMessage RynthLua.EnumValue  18
---@field ItemHeritageGroupRestriction RynthLua.EnumValue  19
---@field PluralName RynthLua.EnumValue  20
---@field MonarchsTitle RynthLua.EnumValue  21
---@field ActivationFailure RynthLua.EnumValue  22
---@field ScribeAccount RynthLua.EnumValue  23
---@field TownName RynthLua.EnumValue  24
---@field CraftsmanName RynthLua.EnumValue  25
---@field UsePkServerError RynthLua.EnumValue  26
---@field ScoreCachedText RynthLua.EnumValue  27
---@field ScoreDefaultEntryFormat RynthLua.EnumValue  28
---@field ScoreFirstEntryFormat RynthLua.EnumValue  29
---@field ScoreLastEntryFormat RynthLua.EnumValue  30
---@field ScoreOnlyEntryFormat RynthLua.EnumValue  31
---@field ScoreNoEntry RynthLua.EnumValue  32
---@field Quest RynthLua.EnumValue  33
---@field GeneratorEvent RynthLua.EnumValue  34
---@field PatronsTitle RynthLua.EnumValue  35
---@field HouseOwnerName RynthLua.EnumValue  36
---@field QuestRestriction RynthLua.EnumValue  37
---@field AppraisalPortalDestination RynthLua.EnumValue  38
---@field TinkerName RynthLua.EnumValue  39
---@field ImbuerName RynthLua.EnumValue  40
---@field HouseOwnerAccount RynthLua.EnumValue  41
---@field DisplayName RynthLua.EnumValue  42
---@field DateOfBirth RynthLua.EnumValue  43
---@field ThirdPartyApi RynthLua.EnumValue  44
---@field KillQuest RynthLua.EnumValue  45
---@field Afk RynthLua.EnumValue  46
---@field AllegianceName RynthLua.EnumValue  47
---@field AugmentationAddQuest RynthLua.EnumValue  48
---@field KillQuest2 RynthLua.EnumValue  49
---@field KillQuest3 RynthLua.EnumValue  50
---@field UseSendsSignal RynthLua.EnumValue  51
---@field GearPlatingName RynthLua.EnumValue  52
---@field PCAPRecordedCurrentMotionState RynthLua.EnumValue  8006
---@field PCAPRecordedServerName RynthLua.EnumValue  8031
---@field PCAPRecordedCharacterName RynthLua.EnumValue  8032
---@field AllegianceMotd RynthLua.EnumValue  9001
---@field AllegianceMotdSetBy RynthLua.EnumValue  9002
---@field AllegianceSpeakerTitle RynthLua.EnumValue  9003
---@field AllegianceSeneschalTitle RynthLua.EnumValue  9004
---@field AllegianceCastellanTitle RynthLua.EnumValue  9005
---@field GodState RynthLua.EnumValue  9006
---@field TinkerLog RynthLua.EnumValue  9007
---@type RynthLua.Enum.StringId
StringId = {}

---Action error names (UtilityBelt's list). `action.Error` is one of these strings; `ActionError.X == "X"`.
---@enum ActionError
ActionError = {
    None = "None",
    TimedOut = "TimedOut",
    ServerError = "ServerError",
    NotLoggedIn = "NotLoggedIn",
    NotEnoughUnassignedExperience = "NotEnoughUnassignedExperience",
    AlreadyMaxed = "AlreadyMaxed",
    TooMuchSpendExperience = "TooMuchSpendExperience",
    NotYourCharacter = "NotYourCharacter",
    NotTrained = "NotTrained",
    YouDoNotPassCraftingRequirements = "YouDoNotPassCraftingRequirements",
    ItemBeingTraded = "ItemBeingTraded",
    YouHaveBeenInPKBattleTooRecently = "YouHaveBeenInPKBattleTooRecently",
    CantUseOnItself = "CantUseOnItself",
    TargetItemNotInInventoryOrLandscape = "TargetItemNotInInventoryOrLandscape",
    SourceItemNotInInventory = "SourceItemNotInInventory",
    SourceItemNotInInventoryOrLandscape = "SourceItemNotInInventoryOrLandscape",
    InvalidTargetItem = "InvalidTargetItem",
    InvalidSourceObject = "InvalidSourceObject",
    ItemAlreadyWielded = "ItemAlreadyWielded",
    TooEncumbered = "TooEncumbered",
    MustBeInPeaceMode = "MustBeInPeaceMode",
    YouCantPickThatUp = "YouCantPickThatUp",
    InvalidInventoryLocation = "InvalidInventoryLocation",
    YouDontKnowThatSpell = "YouDontKnowThatSpell",
    MissingComponents = "MissingComponents",
    UnknownSpellId = "UnknownSpellId",
    SpellFizzled = "SpellFizzled",
    AlreadyInFellow = "AlreadyInFellow",
    MustBeInFellow = "MustBeInFellow",
    NotTheLeader = "NotTheLeader",
    NotInFellow = "NotInFellow",
    MemberDoesntExist = "MemberDoesntExist",
    FellowshipIsFull = "FellowshipIsFull",
    FellowshipClosed = "FellowshipClosed",
    AlreadyRecruited = "AlreadyRecruited",
    FellowshipRecruitBusy = "FellowshipRecruitBusy",
    FellowshipLocked = "FellowshipLocked",
    InvalidTargetObject = "InvalidTargetObject",
    UnknownCommand = "UnknownCommand",
    CharacterDoesntExist = "CharacterDoesntExist",
    CharacterPendingDelete = "CharacterPendingDelete",
    NotAtCharacterSelect = "NotAtCharacterSelect",
    PreconditionFailed = "PreconditionFailed",
    NoUst = "NoUst",
    UnsalvageableItem = "UnsalvageableItem",
    NoTradePartner = "NoTradePartner",
    Exception = "Exception",
    AlreadyTrained = "AlreadyTrained",
    CantAdvanceSkill = "CantAdvanceSkill",
    NotEnoughAvailableCredits = "NotEnoughAvailableCredits",
    SkillDoesntExist = "SkillDoesntExist",
    NotInAnAllegiance = "NotInAnAllegiance",
    AlreadyInAnAllegiance = "AlreadyInAnAllegiance",
    SourceObjectTooFar = "SourceObjectTooFar",
    NoActions = "NoActions",
    Cancelled = "Cancelled",
    NPCDoesntKnowWhatToDoWithThat = "NPCDoesntKnowWhatToDoWithThat",
    TooBusy = "TooBusy",
    VendorNotOpen = "VendorNotOpen",
    VendorDoesntHaveThisItem = "VendorDoesntHaveThisItem",
    VendorDoesntHaveEnoughOfThisItem = "VendorDoesntHaveEnoughOfThisItem",
    CantSplitThisObject = "CantSplitThisObject",
    ObjectSplitAmountTooBig = "ObjectSplitAmountTooBig",
    InvalidSpellLevel = "InvalidSpellLevel",
    InvalidCombatMode = "InvalidCombatMode",
    OnCooldown = "OnCooldown",
}

---Action priorities (UtilityBelt's ActionType): higher runs first. Use as `{ Priority = ActionType.Wield }` or the name as text.
---@enum ActionType
ActionType = {
    Immediate = 2147483647,
    Wield = 20000,
    RestoreHealth = 19000,
    RestoreStamina = 18000,
    RestoreMana = 17000,
    LoginLogoff = 16000,
    Vendor = 15500,
    Trade = 15000,
    Combat = 14000,
    CastSpell = 13000,
    Navigation = 12000,
    Fellow = 11000,
    Allegiance = 10000,
    Inventory = 9000,
    Misc = 8000,
}

------------------------------------------------------------------------------
-- Script windows: Vector2 / Vector4 (globals), require("views"), require("imgui").
-- A hud's OnRender runs about 20 times a second while it is shown and records
-- ImGui calls; the engine draws them every frame. Results (clicks, new values)
-- come back on the next pass. Needs engine API v71 with ImGui on.
------------------------------------------------------------------------------

---A 2D vector: a table with X and Y (x and y read and write the same fields).
---@class Vector2
---@field X number
---@field Y number
---@field x number   Same as X.
---@field y number   Same as Y.
---@operator add(Vector2|number): Vector2
---@operator sub(Vector2|number): Vector2
---@operator mul(Vector2|number): Vector2
---@operator div(Vector2|number): Vector2
---@operator unm: Vector2

---A 4D vector: X, Y, Z, W (also x, y, z, w). As a colour: red, green, blue, alpha, 0 to 1.
---@class Vector4
---@field X number
---@field Y number
---@field Z number
---@field W number
---@field x number
---@field y number
---@field z number
---@field w number
---@operator add(Vector4|number): Vector4
---@operator sub(Vector4|number): Vector4
---@operator mul(Vector4|number): Vector4
---@operator div(Vector4|number): Vector4
---@operator unm: Vector4

---The Vector2 global: Vector2.new(x, y) or Vector2(x, y).
---@class RynthLua.Vector2Global
---@field Zero Vector2   (0, 0), a fresh value each time.
---@field One Vector2    (1, 1), a fresh value each time.
---@overload fun(x?: number, y?: number): Vector2
local Vector2Global = {}
---One number alone fills both components.
---@param x? number
---@param y? number
---@return Vector2
function Vector2Global.new(x, y) end

---The Vector4 global: Vector4.new(x, y, z, w) or Vector4(x, y, z, w).
---@class RynthLua.Vector4Global
---@field Zero Vector4   A fresh value each time.
---@field One Vector4    A fresh value each time.
---@overload fun(x?: number, y?: number, z?: number, w?: number): Vector4
local Vector4Global = {}
---One number alone fills every component.
---@param x? number
---@param y? number
---@param z? number
---@param w? number
---@return Vector4
function Vector4Global.new(x, y, z, w) end

---@type RynthLua.Vector2Global
Vector2 = {}

---@type RynthLua.Vector4Global
Vector4 = {}

---A size or position: a Vector2, {x, y}, {X = x, Y = y} or {x = x, y = y}. 0 = automatic, -1 = fill.
---@alias RynthLua.Vec2Like Vector2|number[]|{X: number, Y: number}|{x: number, y: number}

---A colour: a Vector4 (or {r, g, b, a}) with 0 to 1 components, or a 0xAABBGGRR number.
---@alias RynthLua.Color Vector4|number[]|integer

---@alias RynthLua.HudChrome "standard"|"none"

---A hud render event: handlers get no arguments and can't sleep or await.
---@class RynthLua.Event.Render
---@field Add fun(handler: fun())
---@field Once fun(handler: fun())
---@field Until fun(handler: fun(): boolean?)
---@field Remove fun(handler: function)

---A script window (views.Huds.CreateHud). Other keys are the script's own fields.
---@class RynthLua.Hud
---@field Name string                   Read-only.
---@field Title string                  The header text (default, and nil: the name).
---@field Visible boolean               Starts as the player's last choice for this hud on this character, else false. Set by the script, it isn't remembered.
---@field ShowInBar boolean             Listed in the bar's Scripts menu (default true).
---@field WindowSettings ImGuiWindowFlags|integer  The whole flag set (0 to 4294967295; nil = 0). Honoured: NoTitleBar, NoInputs/NoMouseInputs, NoBackground, NoScrollbar, NoScrollWithMouse.
---@field Chrome RynthLua.HudChrome     "standard" (the RynthCore frame) or "none" (no header or grip).
---@field DefaultSize Vector2           Size while nothing is saved for the window; nil = the engine default. Also takes {x, y}.
---@field RefreshRate integer           Passes per second while shown, 1 to 60 (default 20; nil = 20).
---@field LastError string?             Read-only. The last pass's error, nil once a pass works.
---@field IsDisposed boolean            Read-only.
---@field OnPreRender RynthLua.Event.Render   Runs first in each pass (SetNextWindowSize and friends go here).
---@field OnRender RynthLua.Event.Render      Records the window's contents with ImGui calls.
---@field OnShow RynthLua.Event.Empty         The hud was shown (by anyone).
---@field OnHide RynthLua.Event.Empty         The hud was hidden (by anyone).
---@field [string] any
local Hud = {}

---Removes the hud; its window closes (its saved place is kept).
function Hud.Dispose() end

---views.Huds
---@class RynthLua.HudManager
local HudManager = {}

---This script's hud called `name`; a second call with the same name returns the same hud.
---At most 8 per script and 32 across RynthLua.
---@param name string
---@param icon? any   Accepted and ignored.
---@return RynthLua.Hud
function HudManager.CreateHud(name, icon) end

---A texture for an AC icon (UB's name), for ImGui.Image / ImageButton. An id below
---0x06000000 gets 0x06000000 added, as in UB. A missing icon draws a placeholder.
---@param iconId integer   The 0x06... icon id (wo:Value(DataId.Icon)).
---@return RynthLua.IconTexture
function HudManager.GetIconTexture(iconId) end

---An item's picture as AC shows it: its icon over its underlay, with its overlay on top.
---RynthLua extra.
---@param object RynthLua.WorldObject|integer   A world object or an object id.
---@return RynthLua.IconTexture
function HudManager.GetObjectIconTexture(object) end

---A spell's icon. RynthLua extra.
---@param spell integer|{Id: integer}   A spell id, or a table with an Id.
---@return RynthLua.IconTexture
function HudManager.GetSpellIconTexture(spell) end

---Not supported: RynthLua windows draw AC icons only (raises an error).
---@param file string
function HudManager.CreateTexture(file) end

---An icon texture (views.Huds.Get*IconTexture): names an icon; the engine owns the picture.
---Read-only; the same call returns the same table.
---@class RynthLua.IconTexture
---@field TexturePtr RynthLua.IconTexture   The texture itself (UB: ImGui.Image(tex.TexturePtr, size)).
---@field Kind "icon"|"object"|"spell"
---@field Id integer
local IconTexture = {}

---Does nothing (UB compatibility): the engine's cache frees the pictures.
function IconTexture.Dispose() end

---Does nothing (UB compatibility).
function IconTexture.Release() end

---require("views")
---@class RynthLua.ViewsModule
---@field Huds RynthLua.HudManager
---@field Available boolean   Script windows can show (engine API v71, ImGui on).

---ImGui.GetStyle(): only these two fields; any other is an error.
---@class RynthLua.ImGuiStyle
---@field ItemSpacing Vector2
---@field FramePadding Vector2

---require("imgui"): the calls a hud's OnPreRender / OnRender records. Names, argument
---order and defaults follow ImGui.NET 1.91; `ref` values come back as extra returns.
---Anything not declared here raises "ImGui.X isn't supported by RynthLua windows yet".
---@class RynthLua.ImGui
---@field ImGui RynthLua.ImGui   The same table (UB's require("imgui").ImGui).
local ImGui = {}

---Text. Every argument is turned into text and joined; never a format string.
---@param ... any
function ImGui.Text(...) end

---@param col RynthLua.Color
---@param ... any
function ImGui.TextColored(col, ...) end

---@param ... any
function ImGui.TextWrapped(...) end

---@param ... any
function ImGui.TextDisabled(...) end

---@param ... any
function ImGui.BulletText(...) end

---@param ... any
function ImGui.SeparatorText(...) end

---@param label string
---@param ... any
function ImGui.LabelText(label, ...) end

function ImGui.Separator() end

---@param offset? number    Default 0.
---@param spacing? number   Default -1 (the style's spacing).
function ImGui.SameLine(offset, spacing) end

function ImGui.NewLine() end

function ImGui.Spacing() end

---@param size? RynthLua.Vec2Like
function ImGui.Dummy(size) end

---@param width? number   0 or nil = the style's indent.
function ImGui.Indent(width) end

---@param width? number   0 or nil = the style's indent.
function ImGui.Unindent(width) end

---True on the pass after a click (one click per pass).
---@param label string
---@param size? RynthLua.Vec2Like
---@return boolean clicked
function ImGui.Button(label, size) end

---@param label string
---@return boolean clicked
function ImGui.SmallButton(label) end

---@param label string
---@param value boolean
---@return boolean changed
---@return boolean value
function ImGui.Checkbox(label, value) end

---@param label string
---@param value integer
---@param min? integer   Default 0.
---@param max? integer   Default 100.
---@param format? string Default "%d".
---@return boolean changed
---@return integer value
function ImGui.SliderInt(label, value, min, max, format) end

---@param label string
---@param value number
---@param min? number    Default 0.
---@param max? number    Default 1.
---@param format? string Default "%.3f".
---@return boolean changed
---@return number value
function ImGui.SliderFloat(label, value, min, max, format) end

---The text follows every edit; with ImGuiInputTextFlags.EnterReturnsTrue only `changed` waits for Enter.
---@param label string
---@param text string
---@param maxLength? integer   Bytes, 1 to 4096 (default 256).
---@param flags? ImGuiInputTextFlags|integer
---@return boolean changed
---@return string text
function ImGui.InputText(label, text, maxLength, flags) end

---`index` is 0-based (-1 = none); `items` is a Lua list or ImGui's "a\0b\0c\0" text.
---@param label string
---@param index integer
---@param items string[]|string
---@param count? integer   How many of the list to use (ignored with the text form).
---@return boolean changed
---@return integer index
function ImGui.Combo(label, index, items, count) end

---The second value is `selected` flipped when clicked (UB's ref form).
---@param label string
---@param selected? boolean
---@param flags? ImGuiSelectableFlags|integer
---@param size? RynthLua.Vec2Like
---@return boolean clicked
---@return boolean selected
function ImGui.Selectable(label, selected, flags, size) end

---@param fraction number   0 to 1.
---@param size? RynthLua.Vec2Like   Default (-1, 0): the full width.
---@param overlay? string   Default: ImGui's percentage.
function ImGui.ProgressBar(fraction, size, overlay) end

---A number box with - and + buttons (step 0 = none). With EnterReturnsTrue the value changes on Enter.
---Needs an engine from 2026-09-30 (script windows op level 2); older ones raise an error.
---@param label string
---@param value integer
---@param step? integer      Default 1.
---@param stepFast? integer  Default 100 (Ctrl + click).
---@param flags? ImGuiInputTextFlags|integer   EnterReturnsTrue, ReadOnly, AutoSelectAll, ParseEmptyRefVal, DisplayEmptyRefVal.
---@return boolean changed
---@return integer value
function ImGui.InputInt(label, value, step, stepFast, flags) end

---Unchanged, `value` comes back exactly as given. Op level 2.
---@param label string
---@param value number
---@param step? number       Default 0 (no buttons).
---@param stepFast? number   Default 0.
---@param format? string     Default "%.3f".
---@param flags? ImGuiInputTextFlags|integer
---@return boolean changed
---@return number value
function ImGui.InputFloat(label, value, step, stepFast, format, flags) end

---Drag left and right to change it (double-click to type). min == max: no limits. Op level 2.
---@param label string
---@param value integer
---@param speed? number      Default 1 (per pixel).
---@param min? integer       Default 0.
---@param max? integer       Default 0.
---@param format? string     Default "%d".
---@param flags? ImGuiSliderFlags|integer
---@return boolean changed
---@return integer value
function ImGui.DragInt(label, value, speed, min, max, format, flags) end

---Unchanged, `value` comes back exactly as given. min == max: no limits. Op level 2.
---@param label string
---@param value number
---@param speed? number      Default 1 (per pixel).
---@param min? number        Default 0.
---@param max? number        Default 0.
---@param format? string     Default "%.3f".
---@param flags? ImGuiSliderFlags|integer
---@return boolean changed
---@return number value
function ImGui.DragFloat(label, value, speed, min, max, format, flags) end

---An AC icon. Until the picture is ready, or when it is missing, a dim square of the same size. Op level 2.
---@param texture RynthLua.IconTexture|integer   From views.Huds.Get*IconTexture (or its TexturePtr), or an icon id.
---@param size? RynthLua.Vec2Like    Default 32 x 32.
---@param uv0? RynthLua.Vec2Like     Default (0, 0).
---@param uv1? RynthLua.Vec2Like     Default (1, 1).
---@param tint? RynthLua.Color       Default white.
---@param border? RynthLua.Color     Default none.
function ImGui.Image(texture, size, uv0, uv1, tint, border) end

---A button showing an AC icon. Op level 2.
---@param id string   The button's ID (not shown).
---@param texture RynthLua.IconTexture|integer
---@param size? RynthLua.Vec2Like    Default 32 x 32.
---@param uv0? RynthLua.Vec2Like
---@param uv1? RynthLua.Vec2Like
---@param background? RynthLua.Color   Default none.
---@param tint? RynthLua.Color         Default white.
---@return boolean clicked
function ImGui.ImageButton(id, texture, size, uv0, uv1, background, tint) end

---Always returns true; always call EndChild().
---@param id string|number
---@param size? RynthLua.Vec2Like
---@param border? boolean|ImGuiChildFlags|integer
---@param windowFlags? ImGuiWindowFlags|integer
---@return boolean visible
function ImGui.BeginChild(id, size, border, windowFlags) end

function ImGui.EndChild() end

---The contents follow as ordinary calls while it returns true.
---@param label string
---@param flags? ImGuiTreeNodeFlags|integer
---@return boolean open
function ImGui.CollapsingHeader(label, flags) end

---Call TreePop() only when it returned true.
---@param label string
---@param flags? ImGuiTreeNodeFlags|integer
---@return boolean open
function ImGui.TreeNode(label, flags) end

function ImGui.TreePop() end

---@param id string|number
function ImGui.PushID(id) end

function ImGui.PopID() end

---@param which ImGuiCol|integer|string   One of the allowed colours (see ImGuiCol).
---@param col RynthLua.Color
function ImGui.PushStyleColor(which, col) end

---@param count? integer   Default 1.
function ImGui.PopStyleColor(count) end

---A tooltip for the item before it, shown on hover.
---@param ... any
function ImGui.SetItemTooltip(...) end

---FirstUseEver: a default size; Appearing: when shown; Once: until the first good pass; none/Always: every pass.
---@param size RynthLua.Vec2Like
---@param cond? ImGuiCond|integer
function ImGui.SetNextWindowSize(size, cond) end

---@param pos RynthLua.Vec2Like
---@param cond? ImGuiCond|integer
---@param pivot? RynthLua.Vec2Like   Accepted and ignored.
function ImGui.SetNextWindowPos(pos, cond, pivot) end

---Only the minimum is used; the maximum is ignored.
---@param min RynthLua.Vec2Like
---@param max RynthLua.Vec2Like
function ImGui.SetNextWindowSizeConstraints(min, max) end

---Last frame's value, inside a pass; (0, 0) until the engine first reports it.
---@return Vector2
function ImGui.GetWindowSize() end

---Last frame's value, inside a pass; (0, 0) until the engine first reports it.
---@return Vector2
function ImGui.GetWindowPos() end

---Last frame's value, inside a pass; (0, 0) until the engine first reports it.
---@return Vector2
function ImGui.GetContentRegionAvail() end

---Any time.
---@return number
function ImGui.GetTextLineHeight() end

---Any time.
---@return number
function ImGui.GetTextLineHeightWithSpacing() end

---Any time.
---@return number
function ImGui.GetFrameHeight() end

---Any time.
---@return RynthLua.ImGuiStyle
function ImGui.GetStyle() end

---Any time. Exact for plain ASCII with the default font, an estimate otherwise.
---@param text string
---@param hideTextAfterDoubleHash? boolean
---@param wrapWidth? number   Ignored.
---@return Vector2
function ImGui.CalcTextSize(text, hideTextAfterDoubleHash, wrapWidth) end

---Any time. Puts text on the Windows clipboard; false when another program kept it busy.
---@param text any
---@return boolean ok
function ImGui.SetClipboardText(text) end

---Any time. The clipboard's text, "" when it holds none.
---@return string
function ImGui.GetClipboardText() end

------------------------------------------------------------------------------
-- ImGui enums (globals). Plain two-way tables like the other enums:
-- ImGuiCond.FirstUseEver == 4 and ImGuiWindowFlags[43] == "NoDecoration".
------------------------------------------------------------------------------

---Window flags (ImGui.NET 1.91.6.1), for `hud.WindowSettings` and BeginChild's window flags. Combine with `+` or bit32.bor.
---@enum ImGuiWindowFlags
ImGuiWindowFlags = {
    None = 0,
    NoTitleBar = 1,
    NoResize = 2,
    NoMove = 4,
    NoScrollbar = 8,
    NoScrollWithMouse = 16,
    NoCollapse = 32,
    NoDecoration = 43,
    AlwaysAutoResize = 64,
    NoBackground = 128,
    NoSavedSettings = 256,
    NoMouseInputs = 512,
    MenuBar = 1024,
    HorizontalScrollbar = 2048,
    NoFocusOnAppearing = 4096,
    NoBringToFrontOnFocus = 8192,
    AlwaysVerticalScrollbar = 16384,
    AlwaysHorizontalScrollbar = 32768,
    NoNavInputs = 65536,
    NoNavFocus = 131072,
    NoNav = 196608,
    NoInputs = 197120,
    UnsavedDocument = 262144,
    NoDocking = 524288,
}

---When a SetNextWindowSize / SetNextWindowPos call applies.
---@enum ImGuiCond
ImGuiCond = {
    None = 0,
    Always = 1,
    Once = 2,
    FirstUseEver = 4,
    Appearing = 8,
}

---Style colour ids (ImGui.NET 1.91.6.1). PushStyleColor allows Text, TextDisabled, ChildBg, Border, FrameBg, CheckMark, SliderGrab, Button, ButtonHovered, ButtonActive, Header, HeaderHovered and PlotHistogram.
---@enum ImGuiCol
ImGuiCol = {
    Text = 0,
    TextDisabled = 1,
    WindowBg = 2,
    ChildBg = 3,
    PopupBg = 4,
    Border = 5,
    BorderShadow = 6,
    FrameBg = 7,
    FrameBgHovered = 8,
    FrameBgActive = 9,
    TitleBg = 10,
    TitleBgActive = 11,
    TitleBgCollapsed = 12,
    MenuBarBg = 13,
    ScrollbarBg = 14,
    ScrollbarGrab = 15,
    ScrollbarGrabHovered = 16,
    ScrollbarGrabActive = 17,
    CheckMark = 18,
    SliderGrab = 19,
    SliderGrabActive = 20,
    Button = 21,
    ButtonHovered = 22,
    ButtonActive = 23,
    Header = 24,
    HeaderHovered = 25,
    HeaderActive = 26,
    Separator = 27,
    SeparatorHovered = 28,
    SeparatorActive = 29,
    ResizeGrip = 30,
    ResizeGripHovered = 31,
    ResizeGripActive = 32,
    TabHovered = 33,
    Tab = 34,
    TabSelected = 35,
    TabSelectedOverline = 36,
    TabDimmed = 37,
    TabDimmedSelected = 38,
    TabDimmedSelectedOverline = 39,
    DockingPreview = 40,
    DockingEmptyBg = 41,
    PlotLines = 42,
    PlotLinesHovered = 43,
    PlotHistogram = 44,
    PlotHistogramHovered = 45,
    TableHeaderBg = 46,
    TableBorderStrong = 47,
    TableBorderLight = 48,
    TableRowBg = 49,
    TableRowBgAlt = 50,
    TextLink = 51,
    TextSelectedBg = 52,
    DragDropTarget = 53,
    NavCursor = 54,
    NavWindowingHighlight = 55,
    NavWindowingDimBg = 56,
    ModalWindowDimBg = 57,
    COUNT = 58,
}

---TreeNode / CollapsingHeader flags (ImGui.NET 1.91.6.1). NoTreePushOnOpen is not supported.
---@enum ImGuiTreeNodeFlags
ImGuiTreeNodeFlags = {
    None = 0,
    Selected = 1,
    Framed = 2,
    AllowOverlap = 4,
    NoTreePushOnOpen = 8,
    NoAutoOpenOnLog = 16,
    CollapsingHeader = 26,
    DefaultOpen = 32,
    OpenOnDoubleClick = 64,
    OpenOnArrow = 128,
    Leaf = 256,
    Bullet = 512,
    FramePadding = 1024,
    SpanAvailWidth = 2048,
    SpanFullWidth = 4096,
    SpanTextWidth = 8192,
    SpanAllColumns = 16384,
    NavLeftJumpsBackHere = 32768,
}

---InputText flags (ImGui.NET 1.91.6.1). Drawn: CharsDecimal, CharsHexadecimal, CharsUppercase, CharsNoBlank, EnterReturnsTrue, ReadOnly, Password, AutoSelectAll; the rest are dropped.
---@enum ImGuiInputTextFlags
ImGuiInputTextFlags = {
    None = 0,
    CharsDecimal = 1,
    CharsHexadecimal = 2,
    CharsScientific = 4,
    CharsUppercase = 8,
    CharsNoBlank = 16,
    AllowTabInput = 32,
    EnterReturnsTrue = 64,
    EscapeClearsAll = 128,
    CtrlEnterForNewLine = 256,
    ReadOnly = 512,
    Password = 1024,
    AlwaysOverwrite = 2048,
    AutoSelectAll = 4096,
    ParseEmptyRefVal = 8192,
    DisplayEmptyRefVal = 16384,
    NoHorizontalScroll = 32768,
    NoUndoRedo = 65536,
    ElideLeft = 131072,
}

---Selectable flags (ImGui.NET 1.91.6.1). Drawn: AllowDoubleClick, Disabled, AllowOverlap.
---@enum ImGuiSelectableFlags
ImGuiSelectableFlags = {
    None = 0,
    NoAutoClosePopups = 1,
    SpanAllColumns = 2,
    AllowDoubleClick = 4,
    Disabled = 8,
    AllowOverlap = 16,
    Highlight = 32,
}

---BeginChild flags (ImGui.NET 1.91.6.1): only Borders is used.
---@enum ImGuiChildFlags
ImGuiChildFlags = {
    None = 0,
    Borders = 1,
    AlwaysUseWindowPadding = 2,
    ResizeX = 4,
    ResizeY = 8,
    AutoResizeX = 16,
    AutoResizeY = 32,
    AlwaysAutoResize = 64,
    FrameStyle = 128,
    NavFlattened = 256,
}

---DragInt / DragFloat flags (ImGui.NET 1.91.6.1). All are drawn.
---@enum ImGuiSliderFlags
ImGuiSliderFlags = {
    None = 0,
    Logarithmic = 32,
    NoRoundToFormat = 64,
    NoInput = 128,
    WrapAround = 256,
    ClampOnInput = 512,
    ClampZeroRange = 1024,
    AlwaysClamp = 1536,
}

-- UtilityBelt's other enums (LuaEnumData.cs).

---AmmoType.
---@enum AmmoType
AmmoType = {
    ThrownWeapon = 0,
    Arrow = 1,
    Bolt = 2,
    Dart = 4,
}

---AttackHeight.
---@enum AttackHeight
AttackHeight = {
    High = 1,
    Medium = 2,
    Low = 3,
}

---CharacterOptions1 (flags).
---@enum CharacterOptions1
CharacterOptions1 = {
    u_0x00000001 = 1,
    u_0x00000002 = 2,
    u_0x00000004 = 4,
    u_0x00000008 = 8,
    u_0x00000010 = 16,
    u_0x00000020 = 32,
    u_0x00000040 = 64,
    u_0x00000080 = 128,
    u_0x00000100 = 256,
    u_0x00000200 = 512,
    u_0x00000400 = 1024,
    u_0x00000800 = 2048,
    u_0x00001000 = 4096,
    u_0x00002000 = 8192,
    u_0x00004000 = 16384,
    u_0x00008000 = 32768,
    u_0x00010000 = 65536,
    u_0x00020000 = 131072,
    u_0x00040000 = 262144,
    u_0x00080000 = 524288,
    u_0x00100000 = 1048576,
    u_0x00200000 = 2097152,
    u_0x00400000 = 4194304,
    u_0x00800000 = 8388608,
    u_0x01000000 = 16777216,
    u_0x02000000 = 33554432,
    u_0x04000000 = 67108864,
    u_0x08000000 = 134217728,
    u_0x10000000 = 268435456,
    u_0x20000000 = 536870912,
    u_0x40000000 = 1073741824,
    u_0x80000000 = 2147483648,
}

---CharacterOptions2 (flags).
---@enum CharacterOptions2
CharacterOptions2 = {
    u_0x00000001 = 1,
    u_0x00000002 = 2,
    u_0x00000004 = 4,
    u_0x00000008 = 8,
    u_0x00000010 = 16,
    u_0x00000020 = 32,
    u_0x00000040 = 64,
    u_0x00000080 = 128,
    u_0x00000100 = 256,
    u_0x00000200 = 512,
    u_0x00000400 = 1024,
    u_0x00000800 = 2048,
    u_0x00002000 = 8192,
    u_0x00004000 = 16384,
    u_0x00008000 = 32768,
    u_0x00010000 = 65536,
    u_0x00020000 = 131072,
    u_0x00040000 = 262144,
}

---ContainerProperties.
---@enum ContainerProperties
ContainerProperties = {
    None = 0,
    Container = 1,
    Foci = 2,
}

---CoverageMask (flags).
---@enum CoverageMask
CoverageMask = {
    UpperLegsUnderwear = 2,
    LowerLegsUnderwear = 4,
    ChestUnderwear = 8,
    AbdomenUnderwear = 16,
    UpperArmsUnderwear = 32,
    LowerArmsUnderwear = 64,
    UpperLegs = 256,
    LowerLegs = 512,
    Chest = 1024,
    Abdomen = 2048,
    UpperArms = 4096,
    LowerArms = 8192,
    Head = 16384,
    Hands = 32768,
    Feet = 65536,
}

---CreatureType.
---@enum CreatureType
CreatureType = {
    Invalid = 0,
    Olthoi = 1,
    Banderling = 2,
    Drudge = 3,
    Mosswart = 4,
    Lugian = 5,
    Tumerok = 6,
    Mite = 7,
    Tusker = 8,
    PhyntosWasp = 9,
    Rat = 10,
    Auroch = 11,
    Cow = 12,
    Golem = 13,
    Undead = 14,
    Gromnie = 15,
    Reedshark = 16,
    Armoredillo = 17,
    Fae = 18,
    Virindi = 19,
    Wisp = 20,
    Knathtead = 21,
    Shadow = 22,
    Mattekar = 23,
    Mumiyah = 24,
    Rabbit = 25,
    Sclavus = 26,
    ShallowsShark = 27,
    Monouga = 28,
    Zefir = 29,
    Skeleton = 30,
    Human = 31,
    Shreth = 32,
    Chittick = 33,
    Moarsman = 34,
    OlthoiLarvae = 35,
    Slithis = 36,
    Deru = 37,
    FireElemental = 38,
    Snowman = 39,
    Unknown = 40,
    Bunny = 41,
    LightningElemental = 42,
    Rockslide = 43,
    Grievver = 44,
    Niffis = 45,
    Ursuin = 46,
    Crystal = 47,
    HollowMinion = 48,
    Scarecrow = 49,
    Idol = 50,
    Empyrean = 51,
    Hopeslayer = 52,
    Doll = 53,
    Marionette = 54,
    Carenzi = 55,
    Siraluun = 56,
    AunTumerok = 57,
    HeaTumerok = 58,
    Simulacrum = 59,
    AcidElemental = 60,
    FrostElemental = 61,
    Elemental = 62,
    Statue = 63,
    Wall = 64,
    AlteredHuman = 65,
    Device = 66,
    Harbinger = 67,
    DarkSarcophagus = 68,
    Chicken = 69,
    GotrokLugian = 70,
    Margul = 71,
    BleachedRabbit = 72,
    NastyRabbit = 73,
    GrimacingRabbit = 74,
    Burun = 75,
    Target = 76,
    Ghost = 77,
    Fiun = 78,
    Eater = 79,
    Penguin = 80,
    Ruschk = 81,
    Thrungus = 82,
    ViamontianKnight = 83,
    Remoran = 84,
    Swarm = 85,
    Moar = 86,
    EnchantedArms = 87,
    Sleech = 88,
    Mukkir = 89,
    Merwart = 90,
    Food = 91,
    ParadoxOlthoi = 92,
    Harvest = 93,
    Energy = 94,
    Apparition = 95,
    Aerbax = 96,
    Touched = 97,
    BlightedMoarsman = 98,
    GearKnight = 99,
    Gurog = 100,
    Anekshay = 101,
}

---DamageType (flags).
---@enum DamageType
DamageType = {
    Slashing = 1,
    Piercing = 2,
    Bludgeoning = 4,
    Cold = 8,
    Fire = 16,
    Acid = 32,
    Electric = 64,
}

---DataId (typed values).
---@class RynthLua.Enum.DataId
---@field Undef RynthLua.EnumValue  0
---@field Setup RynthLua.EnumValue  1
---@field MotionTable RynthLua.EnumValue  2
---@field SoundTable RynthLua.EnumValue  3
---@field CombatTable RynthLua.EnumValue  4
---@field QualityFilter RynthLua.EnumValue  5
---@field PaletteBase RynthLua.EnumValue  6
---@field ClothingBase RynthLua.EnumValue  7
---@field Icon RynthLua.EnumValue  8
---@field EyesTexture RynthLua.EnumValue  9
---@field NoseTexture RynthLua.EnumValue  10
---@field MouthTexture RynthLua.EnumValue  11
---@field DefaultEyesTexture RynthLua.EnumValue  12
---@field DefaultNoseTexture RynthLua.EnumValue  13
---@field DefaultMouthTexture RynthLua.EnumValue  14
---@field HairPalette RynthLua.EnumValue  15
---@field EyesPalette RynthLua.EnumValue  16
---@field SkinPalette RynthLua.EnumValue  17
---@field HeadObject RynthLua.EnumValue  18
---@field ActivationAnimation RynthLua.EnumValue  19
---@field InitMotion RynthLua.EnumValue  20
---@field ActivationSound RynthLua.EnumValue  21
---@field PhysicsEffectTable RynthLua.EnumValue  22
---@field UseSound RynthLua.EnumValue  23
---@field UseTargetAnimation RynthLua.EnumValue  24
---@field UseTargetSuccessAnimation RynthLua.EnumValue  25
---@field UseTargetFailureAnimation RynthLua.EnumValue  26
---@field UseUserAnimation RynthLua.EnumValue  27
---@field Spell RynthLua.EnumValue  28
---@field SpellComponent RynthLua.EnumValue  29
---@field PhysicsScript RynthLua.EnumValue  30
---@field LinkedPortalOne RynthLua.EnumValue  31
---@field WieldedTreasureType RynthLua.EnumValue  32
---@field UnknownGuessedname RynthLua.EnumValue  33
---@field UnknownGuessedname2 RynthLua.EnumValue  34
---@field DeathTreasureType RynthLua.EnumValue  35
---@field MutateFilter RynthLua.EnumValue  36
---@field ItemSkillLimit RynthLua.EnumValue  37
---@field UseCreateItem RynthLua.EnumValue  38
---@field DeathSpell RynthLua.EnumValue  39
---@field VendorsClassId RynthLua.EnumValue  40
---@field ItemSpecializedOnly RynthLua.EnumValue  41
---@field HouseId RynthLua.EnumValue  42
---@field AccountHouseId RynthLua.EnumValue  43
---@field RestrictionEffect RynthLua.EnumValue  44
---@field CreationMutationFilter RynthLua.EnumValue  45
---@field TsysMutationFilter RynthLua.EnumValue  46
---@field LastPortal RynthLua.EnumValue  47
---@field LinkedPortalTwo RynthLua.EnumValue  48
---@field OriginalPortal RynthLua.EnumValue  49
---@field IconOverlay RynthLua.EnumValue  50
---@field IconOverlaySecondary RynthLua.EnumValue  51
---@field IconUnderlay RynthLua.EnumValue  52
---@field AugmentationMutationFilter RynthLua.EnumValue  53
---@field AugmentationEffect RynthLua.EnumValue  54
---@field ProcSpell RynthLua.EnumValue  55
---@field AugmentationCreateItem RynthLua.EnumValue  56
---@field AlternateCurrency RynthLua.EnumValue  57
---@field BlueSurgeSpell RynthLua.EnumValue  58
---@field YellowSurgeSpell RynthLua.EnumValue  59
---@field RedSurgeSpell RynthLua.EnumValue  60
---@field OlthoiDeathTreasureType RynthLua.EnumValue  61
---@type RynthLua.Enum.DataId
DataId = {}

---EnchantmentFlags.
---@enum EnchantmentFlags
EnchantmentFlags = {
    Undef = 0,
    Attribute = 1,
    Attribute2nd = 2,
    Int = 4,
    Float = 8,
    Skill = 16,
    BodyDamageValue = 32,
    BodyDamageVariance = 64,
    BodyArmorValue = 128,
    SingleStat = 4096,
    MultipleStat = 8192,
    Multiplicative = 16384,
    Additive = 32768,
    AttackSkills = 65536,
    DefenseSkills = 131072,
    Multiplicative_Degrade = 1048576,
    Additive_Degrade = 2097152,
    Vitae = 8388608,
    Cooldown = 16777216,
    Beneficial = 33554432,
    StatTypes = 255,
}

---EndTradeReason.
---@enum EndTradeReason
EndTradeReason = {
    Normal = 0,
    EnteredCombat = 2,
    Cancelled = 81,
}

---EquipMask (flags).
---@enum EquipMask
EquipMask = {
    None = 0,
    Head = 1,
    ChestUnderwear = 2,
    AbdomenUnderwear = 4,
    UpperArmsUnderwear = 8,
    LowerArmsUnderwear = 16,
    Hands = 32,
    UpperLegsUnderwear = 64,
    LowerLegsUnderwear = 128,
    Feet = 256,
    Chest = 512,
    Abdomen = 1024,
    UpperArms = 2048,
    LowerArms = 4096,
    UpperLegs = 8192,
    LowerLegs = 16384,
    Necklace = 32768,
    RightBracelet = 65536,
    LeftBracelet = 131072,
    RightRing = 262144,
    LeftRing = 524288,
    MeleeWeapon = 1048576,
    Shield = 2097152,
    MissileWeapon = 4194304,
    Ammunition = 8388608,
    Wand = 16777216,
    Cloak = 134217728,
    Trinket = 67108864,
    BlueAetheria = 268435456,
    YellowAetheria = 536870912,
    RedAetheria = 1073741824,
}

---FloatId (typed values).
---@class RynthLua.Enum.FloatId
---@field Undef RynthLua.EnumValue  0
---@field HeartbeatInterval RynthLua.EnumValue  1
---@field HeartbeatTimestamp RynthLua.EnumValue  2
---@field HealthRate RynthLua.EnumValue  3
---@field StaminaRate RynthLua.EnumValue  4
---@field ManaRate RynthLua.EnumValue  5
---@field HealthUponResurrection RynthLua.EnumValue  6
---@field StaminaUponResurrection RynthLua.EnumValue  7
---@field ManaUponResurrection RynthLua.EnumValue  8
---@field StartTime RynthLua.EnumValue  9
---@field StopTime RynthLua.EnumValue  10
---@field ResetInterval RynthLua.EnumValue  11
---@field Shade RynthLua.EnumValue  12
---@field ArmorModVsSlash RynthLua.EnumValue  13
---@field ArmorModVsPierce RynthLua.EnumValue  14
---@field ArmorModVsBludgeon RynthLua.EnumValue  15
---@field ArmorModVsCold RynthLua.EnumValue  16
---@field ArmorModVsFire RynthLua.EnumValue  17
---@field ArmorModVsAcid RynthLua.EnumValue  18
---@field ArmorModVsElectric RynthLua.EnumValue  19
---@field CombatSpeed RynthLua.EnumValue  20
---@field WeaponLength RynthLua.EnumValue  21
---@field DamageVariance RynthLua.EnumValue  22
---@field CurrentPowerMod RynthLua.EnumValue  23
---@field AccuracyMod RynthLua.EnumValue  24
---@field StrengthMod RynthLua.EnumValue  25
---@field MaximumVelocity RynthLua.EnumValue  26
---@field RotationSpeed RynthLua.EnumValue  27
---@field MotionTimestamp RynthLua.EnumValue  28
---@field WeaponDefense RynthLua.EnumValue  29
---@field WimpyLevel RynthLua.EnumValue  30
---@field VisualAwarenessRange RynthLua.EnumValue  31
---@field AuralAwarenessRange RynthLua.EnumValue  32
---@field PerceptionLevel RynthLua.EnumValue  33
---@field PowerupTime RynthLua.EnumValue  34
---@field MaxChargeDistance RynthLua.EnumValue  35
---@field ChargeSpeed RynthLua.EnumValue  36
---@field BuyPrice RynthLua.EnumValue  37
---@field SellPrice RynthLua.EnumValue  38
---@field DefaultScale RynthLua.EnumValue  39
---@field LockpickMod RynthLua.EnumValue  40
---@field RegenerationInterval RynthLua.EnumValue  41
---@field RegenerationTimestamp RynthLua.EnumValue  42
---@field GeneratorRadius RynthLua.EnumValue  43
---@field TimeToRot RynthLua.EnumValue  44
---@field DeathTimestamp RynthLua.EnumValue  45
---@field PkTimestamp RynthLua.EnumValue  46
---@field VictimTimestamp RynthLua.EnumValue  47
---@field LoginTimestamp RynthLua.EnumValue  48
---@field CreationTimestamp RynthLua.EnumValue  49
---@field MinimumTimeSincePk RynthLua.EnumValue  50
---@field DeprecatedHousekeepingPriority RynthLua.EnumValue  51
---@field AbuseLoggingTimestamp RynthLua.EnumValue  52
---@field LastPortalTeleportTimestamp RynthLua.EnumValue  53
---@field UseRadius RynthLua.EnumValue  54
---@field HomeRadius RynthLua.EnumValue  55
---@field ReleasedTimestamp RynthLua.EnumValue  56
---@field MinHomeRadius RynthLua.EnumValue  57
---@field Facing RynthLua.EnumValue  58
---@field ResetTimestamp RynthLua.EnumValue  59
---@field LogoffTimestamp RynthLua.EnumValue  60
---@field EconRecoveryInterval RynthLua.EnumValue  61
---@field WeaponOffense RynthLua.EnumValue  62
---@field DamageMod RynthLua.EnumValue  63
---@field ResistSlash RynthLua.EnumValue  64
---@field ResistPierce RynthLua.EnumValue  65
---@field ResistBludgeon RynthLua.EnumValue  66
---@field ResistFire RynthLua.EnumValue  67
---@field ResistCold RynthLua.EnumValue  68
---@field ResistAcid RynthLua.EnumValue  69
---@field ResistElectric RynthLua.EnumValue  70
---@field ResistHealthBoost RynthLua.EnumValue  71
---@field ResistStaminaDrain RynthLua.EnumValue  72
---@field ResistStaminaBoost RynthLua.EnumValue  73
---@field ResistManaDrain RynthLua.EnumValue  74
---@field ResistManaBoost RynthLua.EnumValue  75
---@field Translucency RynthLua.EnumValue  76
---@field PhysicsScriptIntensity RynthLua.EnumValue  77
---@field Friction RynthLua.EnumValue  78
---@field Elasticity RynthLua.EnumValue  79
---@field AiUseMagicDelay RynthLua.EnumValue  80
---@field ItemMinSpellcraftMod RynthLua.EnumValue  81
---@field ItemMaxSpellcraftMod RynthLua.EnumValue  82
---@field ItemRankProbability RynthLua.EnumValue  83
---@field Shade2 RynthLua.EnumValue  84
---@field Shade3 RynthLua.EnumValue  85
---@field Shade4 RynthLua.EnumValue  86
---@field ItemEfficiency RynthLua.EnumValue  87
---@field ItemManaUpdateTimestamp RynthLua.EnumValue  88
---@field SpellGestureSpeedMod RynthLua.EnumValue  89
---@field SpellStanceSpeedMod RynthLua.EnumValue  90
---@field AllegianceAppraisalTimestamp RynthLua.EnumValue  91
---@field PowerLevel RynthLua.EnumValue  92
---@field AccuracyLevel RynthLua.EnumValue  93
---@field AttackAngle RynthLua.EnumValue  94
---@field AttackTimestamp RynthLua.EnumValue  95
---@field CheckpointTimestamp RynthLua.EnumValue  96
---@field SoldTimestamp RynthLua.EnumValue  97
---@field UseTimestamp RynthLua.EnumValue  98
---@field UseLockTimestamp RynthLua.EnumValue  99
---@field HealkitMod RynthLua.EnumValue  100
---@field FrozenTimestamp RynthLua.EnumValue  101
---@field HealthRateMod RynthLua.EnumValue  102
---@field AllegianceSwearTimestamp RynthLua.EnumValue  103
---@field ObviousRadarRange RynthLua.EnumValue  104
---@field HotspotCycleTime RynthLua.EnumValue  105
---@field HotspotCycleTimeVariance RynthLua.EnumValue  106
---@field SpamTimestamp RynthLua.EnumValue  107
---@field SpamRate RynthLua.EnumValue  108
---@field BondWieldedTreasure RynthLua.EnumValue  109
---@field BulkMod RynthLua.EnumValue  110
---@field SizeMod RynthLua.EnumValue  111
---@field GagTimestamp RynthLua.EnumValue  112
---@field GeneratorUpdateTimestamp RynthLua.EnumValue  113
---@field DeathSpamTimestamp RynthLua.EnumValue  114
---@field DeathSpamRate RynthLua.EnumValue  115
---@field WildAttackProbability RynthLua.EnumValue  116
---@field FocusedProbability RynthLua.EnumValue  117
---@field CrashAndTurnProbability RynthLua.EnumValue  118
---@field CrashAndTurnRadius RynthLua.EnumValue  119
---@field CrashAndTurnBias RynthLua.EnumValue  120
---@field GeneratorInitialDelay RynthLua.EnumValue  121
---@field AiAcquireHealth RynthLua.EnumValue  122
---@field AiAcquireStamina RynthLua.EnumValue  123
---@field AiAcquireMana RynthLua.EnumValue  124
---@field ResistHealthDrain RynthLua.EnumValue  125
---@field LifestoneProtectionTimestamp RynthLua.EnumValue  126
---@field AiCounteractEnchantment RynthLua.EnumValue  127
---@field AiDispelEnchantment RynthLua.EnumValue  128
---@field TradeTimestamp RynthLua.EnumValue  129
---@field AiTargetedDetectionRadius RynthLua.EnumValue  130
---@field EmotePriority RynthLua.EnumValue  131
---@field LastTeleportStartTimestamp RynthLua.EnumValue  132
---@field EventSpamTimestamp RynthLua.EnumValue  133
---@field EventSpamRate RynthLua.EnumValue  134
---@field InventoryOffset RynthLua.EnumValue  135
---@field CriticalMultiplier RynthLua.EnumValue  136
---@field ManaStoneDestroyChance RynthLua.EnumValue  137
---@field SlayerDamageBonus RynthLua.EnumValue  138
---@field AllegianceInfoSpamTimestamp RynthLua.EnumValue  139
---@field AllegianceInfoSpamRate RynthLua.EnumValue  140
---@field NextSpellcastTimestamp RynthLua.EnumValue  141
---@field AppraisalRequestedTimestamp RynthLua.EnumValue  142
---@field AppraisalHeartbeatDueTimestamp RynthLua.EnumValue  143
---@field ManaConversionMod RynthLua.EnumValue  144
---@field LastPkAttackTimestamp RynthLua.EnumValue  145
---@field FellowshipUpdateTimestamp RynthLua.EnumValue  146
---@field CriticalFrequency RynthLua.EnumValue  147
---@field LimboStartTimestamp RynthLua.EnumValue  148
---@field WeaponMissileDefense RynthLua.EnumValue  149
---@field WeaponMagicDefense RynthLua.EnumValue  150
---@field IgnoreShield RynthLua.EnumValue  151
---@field ElementalDamageMod RynthLua.EnumValue  152
---@field StartMissileAttackTimestamp RynthLua.EnumValue  153
---@field LastRareUsedTimestamp RynthLua.EnumValue  154
---@field IgnoreArmor RynthLua.EnumValue  155
---@field ProcSpellRate RynthLua.EnumValue  156
---@field ResistanceModifier RynthLua.EnumValue  157
---@field AllegianceGagTimestamp RynthLua.EnumValue  158
---@field AbsorbMagicDamage RynthLua.EnumValue  159
---@field CachedMaxAbsorbMagicDamage RynthLua.EnumValue  160
---@field GagDuration RynthLua.EnumValue  161
---@field AllegianceGagDuration RynthLua.EnumValue  162
---@field GlobalXpMod RynthLua.EnumValue  163
---@field HealingModifier RynthLua.EnumValue  164
---@field ArmorModVsNether RynthLua.EnumValue  165
---@field ResistNether RynthLua.EnumValue  166
---@field CooldownDuration RynthLua.EnumValue  167
---@field WeaponAuraOffense RynthLua.EnumValue  168
---@field WeaponAuraDefense RynthLua.EnumValue  169
---@field WeaponAuraElemental RynthLua.EnumValue  170
---@field WeaponAuraManaConv RynthLua.EnumValue  171
---@type RynthLua.Enum.FloatId
FloatId = {}

---Gender.
---@enum Gender
Gender = {
    Invalid = 0,
    Male = 1,
    Female = 2,
}

---HeritageGroup.
---@enum HeritageGroup
HeritageGroup = {
    Invalid = 0,
    Aluvian = 1,
    Gharundim = 2,
    Sho = 3,
    Viamontian = 4,
    Shadowbound = 5,
    Gearknight = 6,
    Tumerok = 7,
    Lugian = 8,
    Empyrean = 9,
    Penumbraen = 10,
    Undead = 11,
    Olthoi = 12,
    OlthoiAcid = 13,
}

---IconHighlight (flags).
---@enum IconHighlight
IconHighlight = {
    Invalid = 0,
    Magical = 1,
    Poisoned = 2,
    BoostHealth = 4,
    BoostMana = 8,
    BoostStamina = 16,
    Fire = 32,
    Lightning = 64,
    Frost = 128,
    Acid = 256,
    Bludgeoning = 512,
    Slashing = 1024,
    Piercing = 2048,
    Nether = 4096,
}

---InstanceId (typed values).
---@class RynthLua.Enum.InstanceId
---@field Undef RynthLua.EnumValue  0
---@field Owner RynthLua.EnumValue  1
---@field Container RynthLua.EnumValue  2
---@field Wielder RynthLua.EnumValue  3
---@field Freezer RynthLua.EnumValue  4
---@field Viewer RynthLua.EnumValue  5
---@field Generator RynthLua.EnumValue  6
---@field Scribe RynthLua.EnumValue  7
---@field CurrentCombatTarget RynthLua.EnumValue  8
---@field CurrentEnemy RynthLua.EnumValue  9
---@field ProjectileLauncher RynthLua.EnumValue  10
---@field CurrentAttacker RynthLua.EnumValue  11
---@field CurrentDamager RynthLua.EnumValue  12
---@field CurrentFollowTarget RynthLua.EnumValue  13
---@field CurrentAppraisalTarget RynthLua.EnumValue  14
---@field CurrentFellowshipAppraisalTarget RynthLua.EnumValue  15
---@field ActivationTarget RynthLua.EnumValue  16
---@field Creator RynthLua.EnumValue  17
---@field Victim RynthLua.EnumValue  18
---@field Killer RynthLua.EnumValue  19
---@field Vendor RynthLua.EnumValue  20
---@field Customer RynthLua.EnumValue  21
---@field Bonded RynthLua.EnumValue  22
---@field Wounder RynthLua.EnumValue  23
---@field Allegiance RynthLua.EnumValue  24
---@field Patron RynthLua.EnumValue  25
---@field Monarch RynthLua.EnumValue  26
---@field CombatTarget RynthLua.EnumValue  27
---@field HealthQueryTarget RynthLua.EnumValue  28
---@field LastUnlocker RynthLua.EnumValue  29
---@field CrashAndTurnTarget RynthLua.EnumValue  30
---@field AllowedActivator RynthLua.EnumValue  31
---@field HouseOwner RynthLua.EnumValue  32
---@field House RynthLua.EnumValue  33
---@field Slumlord RynthLua.EnumValue  34
---@field ManaQueryTarget RynthLua.EnumValue  35
---@field CurrentGame RynthLua.EnumValue  36
---@field RequestedAppraisalTarget RynthLua.EnumValue  37
---@field AllowedWielder RynthLua.EnumValue  38
---@field AssignedTarget RynthLua.EnumValue  39
---@field LimboSource RynthLua.EnumValue  40
---@field Snooper RynthLua.EnumValue  41
---@field TeleportedCharacter RynthLua.EnumValue  42
---@field Pet RynthLua.EnumValue  43
---@field PetOwner RynthLua.EnumValue  44
---@field PetDevice RynthLua.EnumValue  45
---@type RynthLua.Enum.InstanceId
InstanceId = {}

---Int64Id (typed values).
---@class RynthLua.Enum.Int64Id
---@field Undef RynthLua.EnumValue  0
---@field TotalExperience RynthLua.EnumValue  1
---@field AvailableExperience RynthLua.EnumValue  2
---@field AugmentationCost RynthLua.EnumValue  3
---@field ItemTotalXp RynthLua.EnumValue  4
---@field ItemBaseXp RynthLua.EnumValue  5
---@field AvailableLuminance RynthLua.EnumValue  6
---@field MaximumLuminance RynthLua.EnumValue  7
---@field InteractionReqs RynthLua.EnumValue  8
---@type RynthLua.Enum.Int64Id
Int64Id = {}

---MagicSchool.
---@enum MagicSchool
MagicSchool = {
    None = 0,
    WarMagic = 1,
    LifeMagic = 2,
    ItemEnchantment = 3,
    CreatureEnchantment = 4,
    VoidMagic = 5,
}

---MaterialType.
---@enum MaterialType
MaterialType = {
    Ceramic = 1,
    Porcelain = 2,
    Linen = 4,
    Satin = 5,
    Silk = 6,
    Velvet = 7,
    Wool = 8,
    Agate = 10,
    Amber = 11,
    Amethyst = 12,
    Aquamarine = 13,
    Azurite = 14,
    BlackGarnet = 15,
    BlackOpal = 16,
    Bloodstone = 17,
    Carnelian = 18,
    Citrine = 19,
    Diamond = 20,
    Emerald = 21,
    FireOpal = 22,
    GreenGarnet = 23,
    GreenJade = 24,
    Hematite = 25,
    ImperialTopaz = 26,
    Jet = 27,
    LapisLazuli = 28,
    LavenderJade = 29,
    Malachite = 30,
    Moonstone = 31,
    Onyx = 32,
    Opal = 33,
    Peridot = 34,
    RedGarnet = 35,
    RedJade = 36,
    RoseQuartz = 37,
    Ruby = 38,
    Sapphire = 39,
    SmokeyQuartz = 40,
    Sunstone = 41,
    TigerEye = 42,
    Tourmaline = 43,
    Turquoise = 44,
    WhiteJade = 45,
    WhiteQuartz = 46,
    WhiteSapphire = 47,
    YellowGarnet = 48,
    YellowTopaz = 49,
    Zircon = 50,
    Ivory = 51,
    Leather = 52,
    ArmoredilloHide = 53,
    GromnieHide = 54,
    ReedSharkHide = 55,
    Brass = 57,
    Bronze = 58,
    Copper = 59,
    Gold = 60,
    Iron = 61,
    Pyreal = 62,
    Silver = 63,
    Steel = 64,
    Alabaster = 66,
    Granite = 67,
    Marble = 68,
    Obsidian = 69,
    Sandstone = 70,
    Serpentine = 71,
    Ebony = 73,
    Mahogany = 74,
    Oak = 75,
    Pine = 76,
    Teak = 77,
}

---MotionStance.
---@enum MotionStance
MotionStance = {
    HandCombat = 60,
    NonCombat = 61,
    SwordCombat = 62,
    BowCombat = 63,
    SwordShieldCombat = 64,
    CrossbowCombat = 65,
    UnusedCombat = 66,
    SlingCombat = 67,
    TwoHandedSwordCombat = 68,
    TwoHandedStaffCombat = 69,
    DualWieldCombat = 70,
    ThrownWeaponCombat = 71,
    Magic = 73,
    BowNoAmmo = 232,
    CrossBowNoAmmo = 233,
    AtlatlCombat = 312,
    ThrownShieldCombat = 313,
}

---ObjectDescriptionFlag (flags).
---@enum ObjectDescriptionFlag
ObjectDescriptionFlag = {
    Openable = 1,
    Inscribable = 2,
    Stuck = 4,
    Player = 8,
    Attackable = 16,
    PlayerKiller = 32,
    HiddenAdmin = 64,
    UiHidden = 128,
    Book = 256,
    Vendor = 512,
    PkSwitch = 1024,
    NpkSwitch = 2048,
    Door = 4096,
    Corpse = 8192,
    LifeStone = 16384,
    Food = 32768,
    Healer = 65536,
    Lockpick = 131072,
    Portal = 262144,
    Admin = 1048576,
    FreePkStatus = 2097152,
    ImmuneCellRestrictions = 4194304,
    RequiresPackSlot = 8388608,
    Retained = 16777216,
    PkLiteStatus = 33554432,
    IncludesSecondHeader = 67108864,
    BindStone = 134217728,
    VolatileRare = 268435456,
    WieldOnUse = 536870912,
    WieldLeft = 1073741824,
}

---ObjectType (typed values).
---@class RynthLua.Enum.ObjectType
---@field MeleeWeapon RynthLua.EnumValue  1
---@field Armor RynthLua.EnumValue  2
---@field Clothing RynthLua.EnumValue  4
---@field Jewelry RynthLua.EnumValue  8
---@field Creature RynthLua.EnumValue  16
---@field Food RynthLua.EnumValue  32
---@field Money RynthLua.EnumValue  64
---@field Misc RynthLua.EnumValue  128
---@field MissileWeapon RynthLua.EnumValue  256
---@field Container RynthLua.EnumValue  512
---@field Useless RynthLua.EnumValue  1024
---@field Gem RynthLua.EnumValue  2048
---@field SpellComponents RynthLua.EnumValue  4096
---@field Writable RynthLua.EnumValue  8192
---@field Key RynthLua.EnumValue  16384
---@field Caster RynthLua.EnumValue  32768
---@field Portal RynthLua.EnumValue  65536
---@field Lockable RynthLua.EnumValue  131072
---@field PromissoryNote RynthLua.EnumValue  262144
---@field ManaStone RynthLua.EnumValue  524288
---@field Service RynthLua.EnumValue  1048576
---@field MagicWieldable RynthLua.EnumValue  2097152
---@field CraftCookingBase RynthLua.EnumValue  4194304
---@field CraftAlchemyBase RynthLua.EnumValue  8388608
---@field CraftFletchingBase RynthLua.EnumValue  33554432
---@field CraftAlchemyIntermediate RynthLua.EnumValue  67108864
---@field CraftFletchingIntermediate RynthLua.EnumValue  134217728
---@field LifeStone RynthLua.EnumValue  268435456
---@field TinkeringTool RynthLua.EnumValue  536870912
---@field TinkeringMaterial RynthLua.EnumValue  1073741824
---@field Gameboard RynthLua.EnumValue  2147483648
---@type RynthLua.Enum.ObjectType
ObjectType = {}

---PhysicsState (flags).
---@enum PhysicsState
PhysicsState = {
    None = 0,
    Static = 1,
    Ethereal = 4,
    ReportCollision = 8,
    IgnoreCollision = 16,
    NoDraw = 32,
    Missle = 64,
    Pushable = 128,
    AlignPath = 256,
    PathClipped = 512,
    Gravity = 1024,
    LightingOn = 2048,
    ParticleEmitter = 4096,
    Hidden = 16384,
    ScriptedCollision = 32768,
    HasPhysicsBsp = 65536,
    Inelastic = 131072,
    HasDefaultAnim = 262144,
    HasDefaultScript = 524288,
    Cloaked = 1048576,
    ReportCollisionAsEnvironment = 2097152,
    EdgeSlide = 4194304,
    Sledding = 8388608,
    Frozen = 16777216,
}

---PlayScript.
---@enum PlayScript
PlayScript = {
    Invalid = 0,
    Test1 = 1,
    Test2 = 2,
    Test3 = 3,
    Launch = 4,
    Explode = 5,
    AttribUpRed = 6,
    AttribDownRed = 7,
    AttribUpOrange = 8,
    AttribDownOrange = 9,
    AttribUpYellow = 10,
    AttribDownYellow = 11,
    AttribUpGreen = 12,
    AttribDownGreen = 13,
    AttribUpBlue = 14,
    AttribDownBlue = 15,
    AttribUpPurple = 16,
    AttribDownPurple = 17,
    SkillUpRed = 18,
    SkillDownRed = 19,
    SkillUpOrange = 20,
    SkillDownOrange = 21,
    SkillUpYellow = 22,
    SkillDownYellow = 23,
    SkillUpGreen = 24,
    SkillDownGreen = 25,
    SkillUpBlue = 26,
    SkillDownBlue = 27,
    SkillUpPurple = 28,
    SkillDownPurple = 29,
    SkillDownBlack = 30,
    HealthUpRed = 31,
    HealthDownRed = 32,
    HealthUpBlue = 33,
    HealthDownBlue = 34,
    HealthUpYellow = 35,
    HealthDownYellow = 36,
    RegenUpRed = 37,
    RegenDownREd = 38,
    RegenUpBlue = 39,
    RegenDownBlue = 40,
    RegenUpYellow = 41,
    RegenDownYellow = 42,
    ShieldUpRed = 43,
    ShieldDownRed = 44,
    ShieldUpOrange = 45,
    ShieldDownOrange = 46,
    ShieldUpYellow = 47,
    ShieldDownYellow = 48,
    ShieldUpGreen = 49,
    ShieldDownGreen = 50,
    ShieldUpBlue = 51,
    ShieldDownBlue = 52,
    ShieldUpPurple = 53,
    ShieldDownPurple = 54,
    ShieldUpGrey = 55,
    ShieldDownGrey = 56,
    EnchantUpRed = 57,
    EnchantDownRed = 58,
    EnchantUpOrange = 59,
    EnchantDownOrange = 60,
    EnchantUpYellow = 61,
    EnchantDownYellow = 62,
    EnchantUpGreen = 63,
    EnchantDownGreen = 64,
    EnchantUpBlue = 65,
    EnchantDownBlue = 66,
    EnchantUpPurple = 67,
    EnchantDownPurple = 68,
    VitaeUpWhite = 69,
    VitaeDownBlack = 70,
    VisionUpWhite = 71,
    VisionDownBlack = 72,
    SwapHealth_Red_To_Yellow = 73,
    SwapHealth_Red_To_Blue = 74,
    SwapHealth_Yellow_To_Red = 75,
    SwapHealth_Yellow_To_Blue = 76,
    SwapHealth_Blue_To_Red = 77,
    SwapHealth_Blue_To_Yellow = 78,
    TransUpWhite = 79,
    TransDownBlack = 80,
    Fizzle = 81,
    PortalEntry = 82,
    PortalExit = 83,
    BreatheFlame = 84,
    BreatheFrost = 85,
    BreatheAcid = 86,
    BreatheLightning = 87,
    Create = 88,
    Destroy = 89,
    ProjectileCollision = 90,
    SplatterLowLeftBack = 91,
    SplatterLowLeftFront = 92,
    SplatterLowRightBack = 93,
    SplatterLowRightFront = 94,
    SplatterMidLeftBack = 95,
    SplatterMidLeftFront = 96,
    SplatterMidRightBack = 97,
    SplatterMidRightFront = 98,
    SplatterUpLeftBack = 99,
    SplatterUpLeftFront = 100,
    SplatterUpRightBack = 101,
    SplatterUpRightFront = 102,
    SparkLowLeftBack = 103,
    SparkLowLeftFront = 104,
    SparkLowRightBack = 105,
    SparkLowRightFront = 106,
    SparkMidLeftBack = 107,
    SparkMidLeftFront = 108,
    SparkMidRightBack = 109,
    SparkMidRightFront = 110,
    SparkUpLeftBack = 111,
    SparkUpLeftFront = 112,
    SparkUpRightBack = 113,
    SparkUpRightFront = 114,
    PortalStorm = 115,
    Hide = 116,
    UnHide = 117,
    Hidden = 118,
    DisappearDestroy = 119,
    SpecialState1 = 120,
    SpecialState2 = 121,
    SpecialState3 = 122,
    SpecialState4 = 123,
    SpecialState5 = 124,
    SpecialState6 = 125,
    SpecialState7 = 126,
    SpecialState8 = 127,
    SpecialState9 = 128,
    SpecialState0 = 129,
    SpecialStateRed = 130,
    SpecialStateOrange = 131,
    SpecialStateYellow = 132,
    SpecialStateGreen = 133,
    SpecialStateBlue = 134,
    SpecialStatePurple = 135,
    SpecialStateWhite = 136,
    SpecialStateBlack = 137,
    LevelUp = 138,
    EnchantUpGrey = 139,
    EnchantDownGrey = 140,
    WeddingBliss = 141,
    EnchantUpWhite = 142,
    EnchantDownWhite = 143,
    CampingMastery = 144,
    CampingIneptitude = 145,
    DispelLife = 146,
    DispelCreature = 147,
    DispelAll = 148,
    BunnySmite = 149,
    BaelZharonSmite = 150,
    WeddingSteele = 151,
    RestrictionEffectBlue = 152,
    RestrictionEffectGreen = 153,
    RestrictionEffectGold = 154,
    LayingofHands = 155,
    AugmentationUseAttribute = 156,
    AugmentationUseSkill = 157,
    AugmentationUseResistances = 158,
    AugmentationUseOther = 159,
    BlackMadness = 160,
    AetheriaLevelUp = 161,
    AetheriaSurgeDestruction = 162,
    AetheriaSurgeProtection = 163,
    AetheriaSurgeRegeneration = 164,
    AetheriaSurgeAffliction = 165,
    AetheriaSurgeFestering = 166,
    HealthDownVoid = 167,
    RegenDownVoid = 168,
    SkillDownVoid = 169,
    DirtyFightingHealDebuff = 170,
    DirtyFightingAttackDebuff = 171,
    DirtyFightingDefenseDebuff = 172,
    DirtyFightingDamageOverTime = 173,
}

---RadarBehavior.
---@enum RadarBehavior
RadarBehavior = {
    Undefined = 0,
    ShowNever = 1,
    ShowMovement = 2,
    ShowAttacking = 3,
    ShowAlways = 4,
}

---RadarColor.
---@enum RadarColor
RadarColor = {
    Default = 0,
    Blue = 1,
    Gold = 2,
    White = 3,
    Purple = 4,
    Red = 5,
    Pink = 6,
    Green = 7,
    Yellow = 8,
    Cyan = 9,
    BrightGreen = 16,
    Admin = 9,
    Advocate = 6,
    Creature = 2,
    LifeStone = 1,
    NPC = 8,
    PlayerKiller = 5,
    Portal = 4,
    Sentinel = 9,
    Vendor = 8,
    Fellowship = 16,
    FellowshipLeader = 16,
    PKLite = 6,
}

---SkillTrainingType.
---@enum SkillTrainingType
SkillTrainingType = {
    Unusable = 0,
    Untrained = 1,
    Trained = 2,
    Specialized = 3,
}

---SpellCategory.
---@enum SpellCategory
SpellCategory = {
    Undef = 0,
    StrengthRaising = 1,
    StrengthLowering = 2,
    EnduranceRaising = 3,
    EnduranceLowering = 4,
    QuicknessRaising = 5,
    QuicknessLowering = 6,
    CoordinationRaising = 7,
    CoordinationLowering = 8,
    FocusRaising = 9,
    FocusLowering = 10,
    SelfRaising = 11,
    SelfLowering = 12,
    FocusConcentration = 13,
    FocusDisruption = 14,
    FocusBrilliance = 15,
    FocusDullness = 16,
    AxeRaising = 17,
    AxeLowering = 18,
    BowRaising = 19,
    BowLowering = 20,
    CrossbowRaising = 21,
    CrossbowLowering = 22,
    DaggerRaising = 23,
    DaggerLowering = 24,
    MaceRaising = 25,
    MaceLowering = 26,
    SpearRaising = 27,
    SpearLowering = 28,
    StaffRaising = 29,
    StaffLowering = 30,
    SwordRaising = 31,
    SwordLowering = 32,
    ThrownWeaponsRaising = 33,
    ThrownWeaponsLowering = 34,
    UnarmedCombatRaising = 35,
    UnarmedCombatLowering = 36,
    MeleeDefenseRaising = 37,
    MeleeDefenseLowering = 38,
    MissileDefenseRaising = 39,
    MissileDefenseLowering = 40,
    MagicDefenseRaising = 41,
    MagicDefenseLowering = 42,
    CreatureEnchantmentRaising = 43,
    CreatureEnchantmentLowering = 44,
    ItemEnchantmentRaising = 45,
    ItemEnchantmentLowering = 46,
    LifeMagicRaising = 47,
    LifeMagicLowering = 48,
    WarMagicRaising = 49,
    WarMagicLowering = 50,
    ManaConversionRaising = 51,
    ManaConversionLowering = 52,
    ArcaneLoreRaising = 53,
    ArcaneLoreLowering = 54,
    AppraiseArmorRaising = 55,
    AppraiseArmorLowering = 56,
    AppraiseItemRaising = 57,
    AppraiseItemLowering = 58,
    AppraiseMagicItemRaising = 59,
    AppraiseMagicItemLowering = 60,
    AppraiseWeaponRaising = 61,
    AppraiseWeaponLowering = 62,
    AssessMonsterRaising = 63,
    AssessMonsterLowering = 64,
    DeceptionRaising = 65,
    DeceptionLowering = 66,
    HealingRaising = 67,
    HealingLowering = 68,
    JumpRaising = 69,
    JumpLowering = 70,
    LeadershipRaising = 71,
    LeadershipLowering = 72,
    LockpickRaising = 73,
    LockpickLowering = 74,
    LoyaltyRaising = 75,
    LoyaltyLowering = 76,
    RunRaising = 77,
    RunLowering = 78,
    HealthRaising = 79,
    HealthLowering = 80,
    StaminaRaising = 81,
    StaminaLowering = 82,
    ManaRaising = 83,
    ManaLowering = 84,
    ManaRemedy = 85,
    ManaMalediction = 86,
    HealthTransfertocaster = 87,
    HealthTransferfromcaster = 88,
    StaminaTransfertocaster = 89,
    StaminaTransferfromcaster = 90,
    ManaTransfertocaster = 91,
    ManaTransferfromcaster = 92,
    HealthAccelerating = 93,
    HealthDecelerating = 94,
    StaminaAccelerating = 95,
    StaminaDecelerating = 96,
    ManaAccelerating = 97,
    ManaDecelerating = 98,
    VitaeRaising = 99,
    VitaeLowering = 100,
    AcidProtection = 101,
    AcidVulnerability = 102,
    BludgeonProtection = 103,
    BludgeonVulnerability = 104,
    ColdProtection = 105,
    ColdVulnerability = 106,
    ElectricProtection = 107,
    ElectricVulnerability = 108,
    FireProtection = 109,
    FireVulnerability = 110,
    PierceProtection = 111,
    PierceVulnerability = 112,
    SlashProtection = 113,
    SlashVulnerability = 114,
    ArmorRaising = 115,
    ArmorLowering = 116,
    AcidMissile = 117,
    BludgeoningMissile = 118,
    ColdMissile = 119,
    ElectricMissile = 120,
    FireMissile = 121,
    PiercingMissile = 122,
    SlashingMissile = 123,
    AcidSeeker = 124,
    BludgeoningSeeker = 125,
    ColdSeeker = 126,
    ElectricSeeker = 127,
    FireSeeker = 128,
    PiercingSeeker = 129,
    SlashingSeeker = 130,
    AcidBurst = 131,
    BludgeoningBurst = 132,
    ColdBurst = 133,
    ElectricBurst = 134,
    FireBurst = 135,
    PiercingBurst = 136,
    SlashingBurst = 137,
    AcidBlast = 138,
    BludgeoningBlast = 139,
    ColdBlast = 140,
    ElectricBlast = 141,
    FireBlast = 142,
    PiercingBlast = 143,
    SlashingBlast = 144,
    AcidScatter = 145,
    BludgeoningScatter = 146,
    ColdScatter = 147,
    ElectricScatter = 148,
    FireScatter = 149,
    PiercingScatter = 150,
    SlashingScatter = 151,
    AttackModRaising = 152,
    AttackModLowering = 153,
    DamageRaising = 154,
    DamageLowering = 155,
    DefenseModRaising = 156,
    DefenseModLowering = 157,
    WeaponTimeRaising = 158,
    WeaponTimeLowering = 159,
    ArmorValueRaising = 160,
    ArmorValueLowering = 161,
    AcidResistanceRaising = 162,
    AcidResistanceLowering = 163,
    BludgeonResistanceRaising = 164,
    BludgeonResistanceLowering = 165,
    ColdResistanceRaising = 166,
    ColdResistanceLowering = 167,
    ElectricResistanceRaising = 168,
    ElectricResistanceLowering = 169,
    FireResistanceRaising = 170,
    FireResistanceLowering = 171,
    PierceResistanceRaising = 172,
    PierceResistanceLowering = 173,
    SlashResistanceRaising = 174,
    SlashResistanceLowering = 175,
    BludgeoningResistanceRaising = 176,
    BludgeoningResistanceLowering = 177,
    SlashingResistanceRaising = 178,
    SlashingResistanceLowering = 179,
    PiercingResistanceRaising = 180,
    PiercingResistanceLowering = 181,
    ElectricalResistanceRaising = 182,
    ElectricalResistanceLowering = 183,
    FrostResistanceRaising = 184,
    FrostResistanceLowering = 185,
    FlameResistanceRaising = 186,
    FlameResistanceLowering = 187,
    AcidicResistanceRaising = 188,
    AcidicResistanceLowering = 189,
    ArmorLevelRaising = 190,
    ArmorLevelLowering = 191,
    LockpickResistanceRaising = 192,
    LockpickResistanceLowering = 193,
    ManaConversionModLowering = 194,
    ManaConversionModRaising = 195,
    VisionRaising = 196,
    VisionLowering = 197,
    TransparencyRaising = 198,
    TransparencyLowering = 199,
    PortalTie = 200,
    PortalRecall = 201,
    PortalCreation = 202,
    PortalItemCreation = 203,
    Vitae = 204,
    AssessPersonRaising = 205,
    AssessPersonLowering = 206,
    AcidVolley = 207,
    BludgeoningVolley = 208,
    FrostVolley = 209,
    LightningVolley = 210,
    FlameVolley = 211,
    ForceVolley = 212,
    BladeVolley = 213,
    PortalSending = 214,
    LifestoneSending = 215,
    CookingRaising = 216,
    CookingLowering = 217,
    FletchingRaising = 218,
    FletchingLowering = 219,
    AlchemyLowering = 220,
    AlchemyRaising = 221,
    AcidRing = 222,
    BludgeoningRing = 223,
    ColdRing = 224,
    ElectricRing = 225,
    FireRing = 226,
    PiercingRing = 227,
    SlashingRing = 228,
    AcidWall = 229,
    BludgeoningWall = 230,
    ColdWall = 231,
    ElectricWall = 232,
    FireWall = 233,
    PiercingWall = 234,
    SlashingWall = 235,
    AcidStrike = 236,
    BludgeoningStrike = 237,
    ColdStrike = 238,
    ElectricStrike = 239,
    FireStrike = 240,
    PiercingStrike = 241,
    SlashingStrike = 242,
    AcidStreak = 243,
    BludgeoningStreak = 244,
    ColdStreak = 245,
    ElectricStreak = 246,
    FireStreak = 247,
    PiercingStreak = 248,
    SlashingStreak = 249,
    Dispel = 250,
    CreatureMysticRaising = 251,
    CreatureMysticLowering = 252,
    ItemMysticRaising = 253,
    ItemMysticLowering = 254,
    WarMysticRaising = 255,
    WarMysticLowering = 256,
    HealthRestoring = 257,
    HealthDepleting = 258,
    ManaRestoring = 259,
    ManaDepleting = 260,
    StrengthIncrease = 261,
    StrengthDecrease = 262,
    EnduranceIncrease = 263,
    EnduranceDecrease = 264,
    QuicknessIncrease = 265,
    QuicknessDecrease = 266,
    CoordinationIncrease = 267,
    CoordinationDecrease = 268,
    FocusIncrease = 269,
    FocusDecrease = 270,
    SelfIncrease = 271,
    SelfDecrease = 272,
    GreatVitalityRaising = 273,
    PoorVitalityLowering = 274,
    GreatVigorRaising = 275,
    PoorVigorLowering = 276,
    GreaterIntellectRaising = 277,
    LessorIntellectLowering = 278,
    LifeGiverRaising = 279,
    LifeTakerLowering = 280,
    StaminaGiverRaising = 281,
    StaminaTakerLowering = 282,
    ManaGiverRaising = 283,
    ManaTakerLowering = 284,
    AcidWardProtection = 285,
    AcidWardVulnerability = 286,
    FireWardProtection = 287,
    FireWardVulnerability = 288,
    ColdWardProtection = 289,
    ColdWardVulnerability = 290,
    ElectricWardProtection = 291,
    ElectricWardVulnerability = 292,
    LeadershipObedienceRaising = 293,
    LeadershipObedienceLowering = 294,
    MeleeDefenseShelterRaising = 295,
    MeleeDefenseShelterLowering = 296,
    MissileDefenseShelterRaising = 297,
    MissileDefenseShelterLowering = 298,
    MagicDefenseShelterRaising = 299,
    MagicDefenseShelterLowering = 300,
    HuntersAcumenRaising = 301,
    HuntersAcumenLowering = 302,
    StillWaterRaising = 303,
    StillWaterLowering = 304,
    StrengthofEarthRaising = 305,
    StrengthofEarthLowering = 306,
    TorrentRaising = 307,
    TorrentLowering = 308,
    GrowthRaising = 309,
    GrowthLowering = 310,
    CascadeAxeRaising = 311,
    CascadeAxeLowering = 312,
    CascadeDaggerRaising = 313,
    CascadeDaggerLowering = 314,
    CascadeMaceRaising = 315,
    CascadeMaceLowering = 316,
    CascadeSpearRaising = 317,
    CascadeSpearLowering = 318,
    CascadeStaffRaising = 319,
    CascadeStaffLowering = 320,
    StoneCliffsRaising = 321,
    StoneCliffsLowering = 322,
    MaxDamageRaising = 323,
    MaxDamageLowering = 324,
    BowDamageRaising = 325,
    BowDamageLowering = 326,
    BowRangeRaising = 327,
    BowRangeLowering = 328,
    ExtraDefenseModRaising = 329,
    ExtraDefenseModLowering = 330,
    ExtraBowSkillRaising = 331,
    ExtraBowSkillLowering = 332,
    ExtraAlchemySkillRaising = 333,
    ExtraAlchemySkillLowering = 334,
    ExtraArcaneLoreSkillRaising = 335,
    ExtraArcaneLoreSkillLowering = 336,
    ExtraAppraiseArmorSkillRaising = 337,
    ExtraAppraiseArmorSkillLowering = 338,
    ExtraCookingSkillRaising = 339,
    ExtraCookingSkillLowering = 340,
    ExtraCrossbowSkillRaising = 341,
    ExtraCrossbowSkillLowering = 342,
    ExtraDeceptionSkillRaising = 343,
    ExtraDeceptionSkillLowering = 344,
    ExtraLoyaltySkillRaising = 345,
    ExtraLoyaltySkillLowering = 346,
    ExtraFletchingSkillRaising = 347,
    ExtraFletchingSkillLowering = 348,
    ExtraHealingSkillRaising = 349,
    ExtraHealingSkillLowering = 350,
    ExtraMeleeDefenseSkillRaising = 351,
    ExtraMeleeDefenseSkillLowering = 352,
    ExtraAppraiseItemSkillRaising = 353,
    ExtraAppraiseItemSkillLowering = 354,
    ExtraJumpingSkillRaising = 355,
    ExtraJumpingSkillLowering = 356,
    ExtraLifeMagicSkillRaising = 357,
    ExtraLifeMagicSkillLowering = 358,
    ExtraLockpickSkillRaising = 359,
    ExtraLockpickSkillLowering = 360,
    ExtraAppraiseMagicItemSkillRaising = 361,
    ExtraAppraiseMagicItemSkillLowering = 362,
    ExtraManaConversionSkillRaising = 363,
    ExtraManaConversionSkillLowering = 364,
    ExtraAssessCreatureSkillRaising = 365,
    ExtraAssessCreatureSkillLowering = 366,
    ExtraAssessPersonSkillRaising = 367,
    ExtraAssessPersonSkillLowering = 368,
    ExtraRunSkillRaising = 369,
    ExtraRunSkillLowering = 370,
    ExtraSwordSkillRaising = 371,
    ExtraSwordSkillLowering = 372,
    ExtraThrownWeaponsSkillRaising = 373,
    ExtraThrownWeaponsSkillLowering = 374,
    ExtraUnarmedCombatSkillRaising = 375,
    ExtraUnarmedCombatSkillLowering = 376,
    ExtraAppraiseWeaponSkillRaising = 377,
    ExtraAppraiseWeaponSkillLowering = 378,
    ArmorIncrease = 379,
    ArmorDecrease = 380,
    ExtraAcidResistanceRaising = 381,
    ExtraAcidResistanceLowering = 382,
    ExtraBludgeonResistanceRaising = 383,
    ExtraBludgeonResistanceLowering = 384,
    ExtraFireResistanceRaising = 385,
    ExtraFireResistanceLowering = 386,
    ExtraColdResistanceRaising = 387,
    ExtraColdResistanceLowering = 388,
    ExtraAttackModRaising = 389,
    ExtraAttackModLowering = 390,
    ExtraArmorValueRaising = 391,
    ExtraArmorValueLowering = 392,
    ExtraPierceResistanceRaising = 393,
    ExtraPierceResistanceLowering = 394,
    ExtraSlashResistanceRaising = 395,
    ExtraSlashResistanceLowering = 396,
    ExtraElectricResistanceRaising = 397,
    ExtraElectricResistanceLowering = 398,
    ExtraWeaponTimeRaising = 399,
    ExtraWeaponTimeLowering = 400,
    BludgeonWardProtection = 401,
    BludgeonWardVulnerability = 402,
    SlashWardProtection = 403,
    SlashWardVulnerability = 404,
    PierceWardProtection = 405,
    PierceWardVulnerability = 406,
    StaminaRestoring = 407,
    StaminaDepleting = 408,
    Fireworks = 409,
    HealthDivide = 410,
    StaminaDivide = 411,
    ManaDivide = 412,
    CoordinationIncrease2 = 413,
    StrengthIncrease2 = 414,
    FocusIncrease2 = 415,
    EnduranceIncrease2 = 416,
    SelfIncrease2 = 417,
    MeleeDefenseMultiply = 418,
    MissileDefenseMultiply = 419,
    MagicDefenseMultiply = 420,
    AttributesDecrease = 421,
    LifeGiverRaising2 = 422,
    ItemEnchantmentRaising2 = 423,
    SkillsDecrease = 424,
    ExtraManaConversionBonus = 425,
    WarMysticRaising2 = 426,
    WarMysticLowering2 = 427,
    MagicDefenseShelterRaising2 = 428,
    ExtraLifeMagicSkillRaising2 = 429,
    CreatureMysticRaising2 = 430,
    ItemMysticRaising2 = 431,
    ManaRaising2 = 432,
    SelfRaising2 = 433,
    CreatureEnchantmentRaising2 = 434,
    SalvagingRaising = 435,
    ExtraSalvagingRaising = 436,
    ExtraSalvagingRaising2 = 437,
    CascadeAxeRaising2 = 438,
    ExtraBowSkillRaising2 = 439,
    ExtraThrownWeaponsSkillRaising2 = 440,
    ExtraCrossbowSkillRaising2 = 441,
    CascadeDaggerRaising2 = 442,
    CascadeMaceRaising2 = 443,
    ExtraUnarmedCombatSkillRaising2 = 444,
    CascadeSpearRaising2 = 445,
    CascadeStaffRaising2 = 446,
    ExtraSwordSkillRaising2 = 447,
    AcidProtectionRare = 448,
    AcidResistanceRaisingRare = 449,
    AlchemyRaisingRare = 450,
    AppraisalResistanceLoweringRare = 451,
    AppraiseArmorRaisingRare = 452,
    AppraiseItemRaisingRare = 453,
    AppraiseMagicItemRaisingRare = 454,
    AppraiseWeaponRaisingRare = 455,
    ArcaneLoreRaisingRare = 456,
    ArmorRaisingRare = 457,
    ArmorValueRaisingRare = 458,
    AssessMonsterRaisingRare = 459,
    AssessPersonRaisingRare = 460,
    AttackModRaisingRare = 461,
    AxeRaisingRare = 462,
    BludgeonProtectionRare = 463,
    BludgeonResistanceRaisingRare = 464,
    BowRaisingRare = 465,
    ColdProtectionRare = 466,
    ColdResistanceRaisingRare = 467,
    CookingRaisingRare = 468,
    CoordinationRaisingRare = 469,
    CreatureEnchantmentRaisingRare = 470,
    CrossbowRaisingRare = 471,
    DaggerRaisingRare = 472,
    DamageRaisingRare = 473,
    DeceptionRaisingRare = 474,
    DefenseModRaisingRare = 475,
    ElectricProtectionRare = 476,
    ElectricResistanceRaisingRare = 477,
    EnduranceRaisingRare = 478,
    FireProtectionRare = 479,
    FireResistanceRaisingRare = 480,
    FletchingRaisingRare = 481,
    FocusRaisingRare = 482,
    HealingRaisingRare = 483,
    HealthAcceleratingRare = 484,
    ItemEnchantmentRaisingRare = 485,
    JumpRaisingRare = 486,
    LeadershipRaisingRare = 487,
    LifeMagicRaisingRare = 488,
    LockpickRaisingRare = 489,
    LoyaltyRaisingRare = 490,
    MaceRaisingRare = 491,
    MagicDefenseRaisingRare = 492,
    ManaAcceleratingRare = 493,
    ManaConversionRaisingRare = 494,
    MeleeDefenseRaisingRare = 495,
    MissileDefenseRaisingRare = 496,
    PierceProtectionRare = 497,
    PierceResistanceRaisingRare = 498,
    QuicknessRaisingRare = 499,
    RunRaisingRare = 500,
    SelfRaisingRare = 501,
    SlashProtectionRare = 502,
    SlashResistanceRaisingRare = 503,
    SpearRaisingRare = 504,
    StaffRaisingRare = 505,
    StaminaAcceleratingRare = 506,
    StrengthRaisingRare = 507,
    SwordRaisingRare = 508,
    ThrownWeaponsRaisingRare = 509,
    UnarmedCombatRaisingRare = 510,
    WarMagicRaisingRare = 511,
    WeaponTimeRaisingRare = 512,
    ArmorIncreaseInkyArmor = 513,
    MagicDefenseShelterRaisingFiun = 514,
    ExtraRunSkillRaisingFiun = 515,
    ExtraManaConversionSkillRaisingFiun = 516,
    AttributesIncreaseCantrip1 = 517,
    ExtraMeleeDefenseSkillRaising2 = 518,
    ACTDPurchaseRewardSpell = 519,
    ACTDPurchaseRewardSpellHealth = 520,
    SaltAshAttackModRaising = 521,
    QuicknessIncrease2 = 522,
    ExtraAlchemySkillRaising2 = 523,
    ExtraCookingSkillRaising2 = 524,
    ExtraFletchingSkillRaising2 = 525,
    ExtraLockpickSkillRaising2 = 526,
    MucorManaWell = 527,
    StaminaRestoring2 = 528,
    AllegianceRaising = 529,
    HealthDoT = 530,
    HealthDoTSecondary = 531,
    HealthDoTTertiary = 532,
    HealthHoT = 533,
    HealthHoTSecondary = 534,
    HealthHoTTertiary = 535,
    HealthDivideSecondary = 536,
    HealthDivideTertiary = 537,
    SetSwordRaising = 538,
    SetAxeRaising = 539,
    SetDaggerRaising = 540,
    SetMaceRaising = 541,
    SetSpearRaising = 542,
    SetStaffRaising = 543,
    SetUnarmedRaising = 544,
    SetBowRaising = 545,
    SetCrossbowRaising = 546,
    SetThrownRaising = 547,
    SetItemEnchantmentRaising = 548,
    SetCreatureEnchantmentRaising = 549,
    SetWarMagicRaising = 550,
    SetLifeMagicRaising = 551,
    SetMeleeDefenseRaising = 552,
    SetMissileDefenseRaising = 553,
    SetMagicDefenseRaising = 554,
    SetStaminaAccelerating = 555,
    SetCookingRaising = 556,
    SetFletchingRaising = 557,
    SetLockpickRaising = 558,
    SetAlchemyRaising = 559,
    SetSalvagingRaising = 560,
    SetArmorExpertiseRaising = 561,
    SetWeaponExpertiseRaising = 562,
    SetItemTinkeringRaising = 563,
    SetMagicItemExpertiseRaising = 564,
    SetLoyaltyRaising = 565,
    SetStrengthRaising = 566,
    SetEnduranceRaising = 567,
    SetCoordinationRaising = 568,
    SetQuicknessRaising = 569,
    SetFocusRaising = 570,
    SetWillpowerRaising = 571,
    SetHealthRaising = 572,
    SetStaminaRaising = 573,
    SetManaRaising = 574,
    SetSprintRaising = 575,
    SetJumpingRaising = 576,
    SetSlashResistanceRaising = 577,
    SetBludgeonResistanceRaising = 578,
    SetPierceResistanceRaising = 579,
    SetFlameResistanceRaising = 580,
    SetAcidResistanceRaising = 581,
    SetFrostResistanceRaising = 582,
    SetLightningResistanceRaising = 583,
    CraftingLockPickRaising = 584,
    CraftingFletchingRaising = 585,
    CraftingCookingRaising = 586,
    CraftingAlchemyRaising = 587,
    CraftingArmorTinkeringRaising = 588,
    CraftingWeaponTinkeringRaising = 589,
    CraftingMagicTinkeringRaising = 590,
    CraftingItemTinkeringRaising = 591,
    SkillPercentAlchemyRaising = 592,
    TwoHandedRaising = 593,
    TwoHandedLowering = 594,
    ExtraTwoHandedSkillRaising = 595,
    ExtraTwoHandedSkillLowering = 596,
    ExtraTwoHandedSkillRaising2 = 597,
    TwoHandedRaisingRare = 598,
    SetTwoHandedRaising = 599,
    GearCraftRaising = 600,
    GearCraftLowering = 601,
    ExtraGearCraftSkillRaising = 602,
    ExtraGearCraftSkillLowering = 603,
    ExtraGearCraftSkillRaising2 = 604,
    GearCraftRaisingRare = 605,
    SetGearCraftRaising = 606,
    LoyaltyManaRaising = 607,
    LoyaltyStaminaRaising = 608,
    LeadershipHealthRaising = 609,
    TrinketDamageRaising = 610,
    TrinketDamageLowering = 611,
    TrinketHealthRaising = 612,
    TrinketStaminaRaising = 613,
    TrinketManaRaising = 614,
    TrinketXPRaising = 615,
    DeceptionArcaneLoreRaising = 616,
    HealOverTimeRaising = 617,
    DamageOverTimeRaising = 618,
    HealingResistRatingRaising = 619,
    AetheriaDamageRatingRaising = 620,
    AetheriaDamageReductionRaising = 621,
    AetheriaHealthRaising = 623,
    AetheriaStaminaRaising = 624,
    AetheriaManaRaising = 625,
    AetheriaCriticalDamageRaising = 626,
    AetheriaHealingAmplificationRaising = 627,
    AetheriaProcDamageRatingRaising = 628,
    AetheriaProcDamageReductionRaising = 629,
    AetheriaProcHealthOverTimeRaising = 630,
    AetheriaProcDamageOverTimeRaising = 631,
    AetheriaProcHealingReductionRaising = 632,
    RareDamageRatingRaising = 633,
    RareDamageReductionRatingRaising = 634,
    AetheriaEnduranceRaising = 635,
    NetherDamageOverTimeRaising = 636,
    NetherDamageOverTimeRaising2 = 637,
    NetherDamageOverTimeRaising3 = 638,
    NetherStreak = 639,
    NetherMissile = 640,
    NetherRing = 641,
    NetherDamageRatingLowering = 642,
    NetherDamageHealingReductionRaising = 643,
    VoidMagicLowering = 644,
    VoidMagicRaising = 645,
    VoidMysticRaising = 646,
    SetVoidMagicRaising = 647,
    VoidMagicRaisingRare = 648,
    VoidMysticRaising2 = 649,
    LuminanceDamageRatingRaising = 650,
    LuminanceDamageReductionRaising = 651,
    LuminanceHealthRaising = 652,
    AetheriaCriticalReductionRaising = 653,
    ExtraMissileDefenseSkillRaising = 654,
    ExtraMissileDefenseSkillLowering = 655,
    ExtraMissileDefenseSkillRaising2 = 656,
    AetheriaHealthResistanceRaising = 657,
    AetheriaDotResistanceRaising = 658,
    CloakSkillRaising = 659,
    CloakAllSkillRaising = 660,
    CloakMagicDefenseLowering = 661,
    CloakMeleeDefenseLowering = 662,
    CloakMissileDefenseLowering = 663,
    DirtyFightingLowering = 664,
    DirtyFightingRaising = 665,
    ExtraDirtyFightingRaising = 666,
    DualWieldLowering = 667,
    DualWieldRaising = 668,
    ExtraDualWieldRaising = 669,
    RecklessnessLowering = 670,
    RecklessnessRaising = 671,
    ExtraRecklessnessRaising = 672,
    ShieldLowering = 673,
    ShieldRaising = 674,
    ExtraShieldRaising = 675,
    SneakAttackLowering = 676,
    SneakAttackRaising = 677,
    ExtraSneakAttackRaising = 678,
    RareDirtyFightingRaising = 679,
    RareDualWieldRaising = 680,
    RareRecklessnessRaising = 681,
    RareShieldRaising = 682,
    RareSneakAttackRaising = 683,
    DFAttackSkillDebuff = 684,
    DFBleedDamage = 685,
    DFDefenseSkillDebuff = 686,
    DFHealingDebuff = 687,
    SetDirtyFightingRaising = 688,
    SetDualWieldRaising = 689,
    SetRecklessnessRaising = 690,
    SetShieldRaising = 691,
    SetSneakAttackRaising = 692,
    LifeGiverMhoire = 693,
    RareDamageRatingRaising2 = 694,
    SpellDamageRaising = 695,
    SummoningRaising = 696,
    SummoningLowering = 697,
    ExtraSummoningSkillRaising = 698,
    SetSummoningRaising = 699,
    ParagonEnduranceRaising = 704,
    ParagonManaRaising = 705,
    ParagonStaminaRaising = 706,
    ParagonDirtyFightingRaising = 707,
    ParagonDualWieldRaising = 708,
    ParagonRecklessnessRaising = 709,
    ParagonSneakAttackRaising = 710,
    ParagonDamageRatingRaising = 711,
    ParagonDamageReductionRatingRaising = 712,
    ParagonCriticalDamageRatingRaising = 713,
    ParagonCriticalDamageReductionRatingRaising = 714,
    ParagonAxeRaising = 715,
    ParagonDaggerRaising = 716,
    ParagonSwordRaising = 717,
    ParagonWarMagicRaising = 718,
    ParagonLifeMagicRaising = 719,
    ParagonVoidMagicRaising = 720,
    ParagonBowRaising = 721,
    ParagonStrengthRaising = 722,
    ParagonCoordinationRaising = 723,
    ParagonQuicknessRaising = 724,
    ParagonFocusRaising = 725,
    ParagonWillpowerRaising = 726,
    ParagonTwoHandedRaising = 727,
    GauntletDamageReductionRatingRaising = 728,
    GauntletDamageRatingRaising = 729,
    GauntletHealingRatingRaising = 730,
    GauntletVitalityRaising = 731,
    GauntletCriticalDamageRatingRaising = 732,
    GauntletCriticalDamageReductionRatingRaising = 733,
}

---SpellFlags (flags).
---@enum SpellFlags
SpellFlags = {
    Resistable = 1,
    PKSensitive = 2,
    Beneficial = 4,
    SelfTargeted = 8,
    Reversed = 16,
    NotIndoor = 32,
    NotOutdoor = 64,
    NotResearchable = 128,
    Projectile = 256,
    CreatureSpell = 512,
    ExcludedFromItemDescriptions = 1024,
    IgnoresManaConversion = 2048,
    NonTrackingProjectile = 4096,
    FellowshipSpell = 8192,
    FastCast = 16384,
    IndoorLongRange = 32768,
    DamageOverTime = 65536,
    UNKNOWN = 131072,
}

---SpellType.
---@enum SpellType
SpellType = {
    Undef = 0,
    Enchantment = 1,
    Projectile = 2,
    Boost = 3,
    Transfer = 4,
    PortalLink = 5,
    PortalRecall = 6,
    PortalSummon = 7,
    PortalSending = 8,
    Dispel = 9,
    LifeProjectile = 10,
    FellowBoost = 11,
    FellowEnchantment = 12,
    FellowPortalSending = 13,
    FellowDispel = 14,
    EnchantmentProjectile = 15,
}

---TradeSide.
---@enum TradeSide
TradeSide = {
    Self = 1,
    Partner = 2,
}

---UsableType (flags).
---@enum UsableType
UsableType = {
    SourceUnusable = 1,
    SourceSelf = 2,
    SourceWielded = 4,
    SourceContained = 8,
    SourceViewed = 16,
    SourceRemote = 32,
    SourceNoApproach = 64,
    SourceObjectSelf = 128,
    TargetUnusable = 65536,
    TargetSelf = 131072,
    TargetWielded = 262144,
    TargetContained = 524288,
    TargetViewed = 1048576,
    TargetRemote = 2097152,
    TargetNoApproach = 4194304,
    TargetObjectSelf = 8388608,
}

---Vital.
---@enum Vital
Vital = {
    Undef = 0,
    MaxHealth = 1,
    Health = 2,
    MaxStamina = 3,
    Stamina = 4,
    MaxMana = 5,
    Mana = 6,
}

---WeenieError.
---@enum WeenieError
WeenieError = {
    None = 0,
    NoMem = 1,
    BadParam = 2,
    DivZero = 3,
    SegV = 4,
    Unimplemented = 5,
    UnknownMessageType = 6,
    NoAnimationTable = 7,
    NoPhysicsObject = 8,
    NoBookieObject = 9,
    NoWslObject = 10,
    NoMotionInterpreter = 11,
    UnhandledSwitch = 12,
    DefaultConstructorCalled = 13,
    InvalidCombatManeuver = 14,
    BadCast = 15,
    MissingQuality = 16,
    MissingDatabaseObject = 18,
    NoCallbackSet = 19,
    CorruptQuality = 20,
    BadContext = 21,
    NoEphseqManager = 22,
    BadMovementEvent = 23,
    CannotCreateNewObject = 24,
    NoControllerObject = 25,
    CannotSendEvent = 26,
    PhysicsCantTransition = 27,
    PhysicsMaxDistanceExceeded = 28,
    YoureTooBusy = 29,
    CannotSendMessage = 31,
    IllegalInventoryTransaction = 32,
    ExternalWeenieObject = 33,
    InternalWeenieObject = 34,
    MotionFailure = 35,
    YouCantJumpWhileInTheAir = 36,
    InqCylSphereFailure = 37,
    ThatIsNotAValidCommand = 38,
    CarryingItem = 39,
    Frozen = 40,
    Stuck = 41,
    YouAreTooEncumbered = 42,
    BadContain = 44,
    BadParent = 45,
    BadDrop = 46,
    BadRelease = 47,
    MsgBadMsg = 48,
    MsgUnpackFailed = 49,
    MsgNoMsg = 50,
    MsgUnderflow = 51,
    MsgOverflow = 52,
    MsgCallbackFailed = 53,
    ActionCancelled = 54,
    ObjectGone = 55,
    NoObject = 56,
    CantGetThere = 57,
    Dead = 58,
    ILeftTheWorld = 59,
    ITeleported = 60,
    YouChargedTooFar = 61,
    YouAreTooTiredToDoThat = 62,
    CantCrouchInCombat = 63,
    CantSitInCombat = 64,
    CantLieDownInCombat = 65,
    CantChatEmoteInCombat = 66,
    NoMtableData = 67,
    CantChatEmoteNotStanding = 68,
    TooManyActions = 69,
    Hidden = 70,
    GeneralMovementFailure = 71,
    YouCantJumpFromThisPosition = 72,
    CantJumpLoadedDown = 73,
    YouKilledYourself = 74,
    MsgResponseFailure = 75,
    ObjectIsStatic = 76,
    InvalidPkStatus = 77,
    InvalidXpAmount = 1001,
    InvalidPpCalculation = 1002,
    InvalidCpCalculation = 1003,
    UnhandledStatAnswer = 1004,
    HeartAttack = 1005,
    TheContainerIsClosed = 1006,
    InvalidInventoryLocation = 1008,
    ChangeCombatModeFailure = 1009,
    FullInventoryLocation = 1010,
    ConflictingInventoryLocation = 1011,
    ItemNotPending = 1012,
    BeWieldedFailure = 1013,
    BeDroppedFailure = 1014,
    YouAreTooFatiguedToAttack = 1015,
    YouAreOutOfAmmunition = 1016,
    YourAttackMisfired = 1017,
    YouveAttemptedAnImpossibleSpellPath = 1018,
    MagicIncompleteAnimList = 1019,
    MagicInvalidSpellType = 1020,
    MagicInqPositionAndVelocityFailure = 1021,
    YouDontKnowThatSpell = 1022,
    IncorrectTargetType = 1023,
    YouDontHaveAllTheComponents = 1024,
    YouDontHaveEnoughManaToCast = 1025,
    YourSpellFizzled = 1026,
    YourSpellTargetIsMissing = 1027,
    YourProjectileSpellMislaunched = 1028,
    MagicSpellbookAddSpellFailure = 1029,
    MagicTargetOutOfRange = 1030,
    YourSpellCannotBeCastOutside = 1031,
    YourSpellCannotBeCastInside = 1032,
    MagicGeneralFailure = 1033,
    YouAreUnpreparedToCastASpell = 1034,
    YouveAlreadySwornAllegiance = 1035,
    CantSwearAllegianceInsufficientXp = 1036,
    AllegianceIgnoringRequests = 1037,
    AllegianceSquelched = 1038,
    AllegianceMaxDistanceExceeded = 1039,
    AllegianceIllegalLevel = 1040,
    AllegianceBadCreation = 1041,
    AllegiancePatronBusy = 1042,
    YouAreNotInAllegiance = 1044,
    AllegianceRemoveHierarchyFailure = 1045,
    FellowshipIgnoringRequests = 1047,
    FellowshipSquelched = 1048,
    FellowshipMaxDistanceExceeded = 1049,
    FellowshipMember = 1050,
    FellowshipIllegalLevel = 1051,
    FellowshipRecruitBusy = 1052,
    YouMustBeLeaderOfFellowship = 1053,
    YourFellowshipIsFull = 1054,
    FellowshipNameIsNotPermitted = 1055,
    LevelTooLow = 1056,
    LevelTooHigh = 1057,
    ThatChannelDoesntExist = 1058,
    YouCantUseThatChannel = 1059,
    YouAreAlreadyOnThatChannel = 1060,
    YouAreNotOnThatChannel = 1061,
    AttunedItem = 1062,
    YouCannotMergeDifferentStacks = 1063,
    YouCannotMergeEnchantedItems = 1064,
    YouMustControlAtLeastOneStack = 1065,
    CurrentlyAttacking = 1066,
    MissileAttackNotOk = 1067,
    TargetNotAcquired = 1068,
    ImpossibleShot = 1069,
    BadWeaponSkill = 1070,
    UnwieldFailure = 1071,
    LaunchFailure = 1072,
    ReloadFailure = 1073,
    UnableToMakeCraftReq = 1074,
    CraftAnimationFailed = 1075,
    YouCantCraftWithThatNumberOfItems = 1076,
    CraftGeneralErrorUiMsg = 1077,
    CraftGeneralErrorNoUiMsg = 1078,
    YouDoNotPassCraftingRequirements = 1079,
    YouDoNotHaveAllTheNecessaryItems = 1080,
    NotAllTheItemsAreAvailable = 1081,
    YouMustBeInPeaceModeToTrade = 1082,
    YouAreNotTrainedInThatTradeSkill = 1083,
    YourHandsMustBeFree = 1084,
    YouCannotLinkToThatPortal = 1085,
    YouHaveSolvedThisQuestTooRecently = 1086,
    YouHaveSolvedThisQuestTooManyTimes = 1087,
    QuestUnknown = 1088,
    QuestTableCorrupt = 1089,
    QuestBad = 1090,
    QuestDuplicate = 1091,
    QuestUnsolved = 1092,
    ItemRequiresQuestToBePickedUp = 1093,
    QuestSolvedTooLongAgo = 1094,
    TradeIgnoringRequests = 1100,
    TradeSquelched = 1101,
    TradeMaxDistanceExceeded = 1102,
    TradeAlreadyTrading = 1103,
    TradeBusy = 1104,
    TradeClosed = 1105,
    TradeExpired = 1106,
    TradeItemBeingTraded = 1107,
    TradeNonEmptyContainer = 1108,
    TradeNonCombatMode = 1109,
    TradeIncomplete = 1110,
    TradeStampMismatch = 1111,
    TradeUnopened = 1112,
    TradeEmpty = 1113,
    TradeAlreadyAccepted = 1114,
    TradeOutOfSync = 1115,
    PKsMayNotUsePortal = 1116,
    NonPKsMayNotUsePortal = 1117,
    HouseAbandoned = 1118,
    HouseEvicted = 1119,
    HouseAlreadyOwned = 1120,
    HouseBuyFailed = 1121,
    HouseRentFailed = 1122,
    Hooked = 1123,
    MagicInvalidPosition = 1125,
    YouMustHaveDarkMajestyToUsePortal = 1126,
    InvalidAmmoType = 1127,
    SkillTooLow = 1128,
    YouHaveUsedAllTheHooks = 1129,
    TradeAiDoesntWant = 1130,
    HookHouseNotOwned = 1131,
    YouMustCompleteQuestToUsePortal = 1140,
    HouseNoAllegiance = 1150,
    YouMustOwnHouseToUseCommand = 1151,
    YourMonarchDoesNotOwnAMansionOrVilla = 1152,
    YourMonarchsHouseIsNotAMansionOrVilla = 1153,
    YourMonarchHasClosedTheMansion = 1154,
    YouMustBeMonarchToPurchaseDwelling = 1162,
    AllegianceTimeout = 1165,
    YourOfferOfAllegianceWasIgnored = 1166,
    ConfirmationInProgress = 1167,
    YouMustBeAMonarchToUseCommand = 1168,
    YouMustSpecifyCharacterToBoot = 1169,
    YouCantBootYourself = 1170,
    ThatCharacterDoesNotExist = 1171,
    ThatPersonIsNotInYourAllegiance = 1172,
    CantBreakFromPatronNotInAllegiance = 1173,
    YourAllegianceHasBeenDissolved = 1174,
    YourPatronsAllegianceHasBeenBroken = 1175,
    YouHaveMovedTooFar = 1176,
    TeleToInvalidPosition = 1177,
    MustHaveDarkMajestyToUse = 1178,
    YouFailToLinkWithLifestone = 1179,
    YouWanderedTooFarToLinkWithLifestone = 1180,
    YouSuccessfullyLinkWithLifestone = 1181,
    YouMustLinkToLifestoneToRecall = 1182,
    YouFailToRecallToLifestone = 1183,
    YouFailToLinkWithPortal = 1184,
    YouSuccessfullyLinkWithPortal = 1185,
    YouFailToRecallToPortal = 1186,
    YouMustLinkToPortalToRecall = 1187,
    YouFailToSummonPortal = 1188,
    YouMustLinkToPortalToSummonIt = 1189,
    YouFailToTeleport = 1190,
    YouHaveBeenTeleportedTooRecently = 1191,
    YouMustBeAnAdvocateToUsePortal = 1192,
    PortalAisNotAllowed = 1193,
    PlayersMayNotUsePortal = 1194,
    YouAreNotPowerfulEnoughToUsePortal = 1195,
    YouAreTooPowerfulToUsePortal = 1196,
    YouCannotRecallPortal = 1197,
    YouCannotSummonPortal = 1198,
    LockAlreadyUnlocked = 1199,
    YouCannotLockOrUnlockThat = 1200,
    YouCannotLockWhatIsOpen = 1201,
    KeyDoesntFitThisLock = 1202,
    LockUsedTooRecently = 1203,
    YouArentTrainedInLockpicking = 1204,
    AllegianceInfoEmptyName = 1205,
    AllegianceInfoSelf = 1206,
    AllegianceInfoTooRecent = 1207,
    AbuseNoSuchCharacter = 1208,
    AbuseReportedSelf = 1209,
    AbuseComplaintHandled = 1210,
    YouDoNotOwnThatSalvageTool = 1213,
    YouDoNotOwnThatItem = 1214,
    MaterialCannotBeCreated = 1217,
    ItemsAttemptingToSalvageIsInvalid = 1218,
    YouCannotSalvageItemsInTrading = 1219,
    YouMustBeHouseGuestToUsePortal = 1220,
    YourAllegianceRankIsTooLowToUseMagic = 1221,
    YourArcaneLoreIsTooLowToUseMagic = 1223,
    ItemDoesntHaveEnoughMana = 1224,
    YouHaveBeenInPKBattleTooRecently = 1228,
    TradeAiRefuseEmote = 1229,
    YouFailToAlterSkill = 1232,
    FellowshipDeclined = 1243,
    FellowshipTimeout = 1244,
    YouHaveFailedToAlterAttributes = 1245,
    CannotTransferAttributesWhileWieldingItem = 1248,
    YouHaveSucceededTransferringAttributes = 1249,
    HookIsDuplicated = 1250,
    ItemIsWrongTypeForHook = 1251,
    HousingChestIsDuplicated = 1252,
    HookWillBeDeleted = 1253,
    HousingChestWillBeDeleted = 1254,
    CannotSwearAllegianceWhileOwningMansion = 1255,
    YouCantDoThatWhileInTheAir = 1259,
    CannotChangePKStatusWhileRecovering = 1260,
    AdvocatesCannotChangePKStatus = 1261,
    LevelTooLowToChangePKStatusWithObject = 1262,
    LevelTooHighToChangePKStatusWithObject = 1263,
    YouFeelAHarshDissonance = 1264,
    YouArePKAgain = 1265,
    YouAreTemporarilyNoLongerPK = 1266,
    PKLiteMayNotUsePortal = 1267,
    YouArentTrainedInHealing = 1276,
    YouDontOwnThatHealingKit = 1277,
    YouCantHealThat = 1278,
    YouArentReadyToHeal = 1280,
    YouCanOnlyHealPlayers = 1281,
    LifestoneMagicProtectsYou = 1282,
    PortalEnergyProtectsYou = 1283,
    YouAreNonPKAgain = 1284,
    YoureTooCloseToYourSanctuary = 1285,
    CantDoThatTradeInProgress = 1286,
    OnlyNonPKsMayEnterPKLite = 1287,
    YouAreNowPKLite = 1288,
    YouDoNotBelongToAFellowship = 1295,
    UsingMaxHooksSilent = 1297,
    YouAreNowUsingMaxHooks = 1298,
    YouAreNoLongerUsingMaxHooks = 1299,
    YouAreNotPermittedToUseThatHook = 1302,
    LockedFellowshipCannotRecruitYou = 1305,
    ActivationNotAllowedNotOwner = 1306,
    TurbineChatIsEnabled = 1309,
    YouCannotAddPeopleToHearList = 1312,
    YouAreNowDeafTo_Screams = 1315,
    YouCanHearAllPlayersOnceAgain = 1316,
    YouChickenOut = 1318,
    YouCanPossiblySucceed = 1319,
    FellowshipIsLocked = 1320,
    TradeComplete = 1321,
    NotASalvageTool = 1322,
    CharacterNotAvailable = 1323,
    YouMustWaitToPurchaseHouse = 1330,
    YouDoNotHaveAuthorityInAllegiance = 1333,
    YouHaveMaxAccountsBanned = 1344,
    YouHaveMaxAllegianceOfficers = 1349,
    YourAllegianceOfficersHaveBeenCleared = 1350,
    YouCannotJoinChannelsWhileGagged = 1352,
    YouAreNoLongerAllegianceOfficer = 1354,
    YourAllegianceDoesNotHaveHometown = 1356,
    HookItemNotUsable_CannotOpen = 1358,
    HookItemNotUsable_CanOpen = 1359,
    MissileOutOfRange = 1360,
    MustPurchaseThroneOfDestinyToUseFunction = 1362,
    MustPurchaseThroneOfDestinyToUseItem = 1363,
    MustPurchaseThroneOfDestinyToUsePortal = 1364,
    MustPurchaseThroneOfDestinyToAccessQuest = 1365,
    YouFailedToCompleteAugmentation = 1366,
    AugmentationUsedTooManyTimes = 1367,
    AugmentationTypeUsedTooManyTimes = 1368,
    AugmentationNotEnoughExperience = 1369,
    ExitTrainingAcademyToUseCommand = 1373,
    OnlyPKsMayUseCommand = 1375,
    OnlyPKLiteMayUseCommand = 1376,
    MaxFriendsExceeded = 1377,
    ThatCharacterNotOnYourFriendsList = 1379,
    OnlyHouseOwnerCanUseCommand = 1380,
    InvalidAllegianceNameCantBeEmpty = 1381,
    InvalidAllegianceNameTooLong = 1382,
    InvalidAllegianceNameBadCharacters = 1383,
    InvalidAllegianceNameInappropriate = 1384,
    InvalidAllegianceNameAlreadyInUse = 1385,
    AllegianceNameCleared = 1387,
    InvalidAllegianceNameSameName = 1388,
    InvalidOfficerLevel = 1391,
    AllegianceOfficerTitleIsNotAppropriate = 1392,
    AllegianceNameIsTooLong = 1393,
    AllegianceOfficerTitlesCleared = 1394,
    AllegianceTitleHasIllegalChars = 1395,
    YouHaveNotPreApprovedVassals = 1401,
    YouHaveClearedPreApprovedVassal = 1404,
    CharIsAlreadyGagged = 1405,
    CharIsNotCurrentlyGagged = 1406,
    YourAllegianceChatPrivilegesRestored = 1409,
    TooManyUniqueItems = 1412,
    HeritageRequiresSpecificArmor = 1413,
    ArmorRequiresSpecificHeritage = 1414,
    OlthoiCannotInteractWithThat = 1415,
    OlthoiCannotUseLifestones = 1416,
    OlthoiVendorLooksInHorror = 1417,
    OlthoiCannotJoinFellowship = 1419,
    OlthoiCannotJoinAllegiance = 1420,
    YouCannotUseThatItem = 1421,
    ThisPersonWillNotInteractWithYou = 1422,
    OnlyOlthoiMayUsePortal = 1423,
    OlthoiMayNotUsePortal = 1424,
    YouMayNotUsePortalWithVitae = 1425,
    YouMustBeTwoWeeksOldToUsePortal = 1426,
    OlthoiCanOnlyRecallToLifestone = 1427,
    ContractError = 1428,
}

---WeenieType.
---@enum WeenieType
WeenieType = {
    Undef = 0,
    Generic = 1,
    Clothing = 2,
    MissileLauncher = 3,
    Missile = 4,
    Ammunition = 5,
    MeleeWeapon = 6,
    Portal = 7,
    Book = 8,
    Coin = 9,
    Creature = 10,
    Admin = 11,
    Vendor = 12,
    HotSpot = 13,
    Corpse = 14,
    Cow = 15,
    AI = 16,
    Machine = 17,
    Food = 18,
    Door = 19,
    Chest = 20,
    Container = 21,
    Key = 22,
    Lockpick = 23,
    PressurePlate = 24,
    LifeStone = 25,
    Switch = 26,
    PKModifier = 27,
    Healer = 28,
    LightSource = 29,
    Allegiance = 30,
    UNKNOWN__GUESSEDNAME32 = 31,
    SpellComponent = 32,
    ProjectileSpell = 33,
    Scroll = 34,
    Caster = 35,
    Channel = 36,
    ManaStone = 37,
    Gem = 38,
    AdvocateFane = 39,
    AdvocateItem = 40,
    Sentinel = 41,
    GSpellEconomy = 42,
    LSpellEconomy = 43,
    CraftTool = 44,
    LScoreKeeper = 45,
    GScoreKeeper = 46,
    GScoreGatherer = 47,
    ScoreBook = 48,
    EventCoordinator = 49,
    Entity = 50,
    Stackable = 51,
    HUD = 52,
    House = 53,
    Deed = 54,
    SlumLord = 55,
    Hook = 56,
    Storage = 57,
    BootSpot = 58,
    HousePortal = 59,
    Game = 60,
    GamePiece = 61,
    SkillAlterationDevice = 62,
    AttributeTransferDevice = 63,
    Hooker = 64,
    AllegianceBindstone = 65,
    InGameStatKeeper = 66,
    AugmentationDevice = 67,
    SocialManager = 68,
    Pet = 69,
    PetDevice = 70,
    CombatPet = 71,
}

---WieldType (flags).
---@enum WieldType
WieldType = {
    Invalid = 0,
    MeleeWeapon = 1,
    Armor = 2,
    Clothing = 4,
    Jewelry = 8,
}

---AddRemoveEventType.
---@enum AddRemoveEventType
AddRemoveEventType = {
    Added = 0,
    Removed = 1,
}

---BusyAction.
---@enum BusyAction
BusyAction = {
    None = 0,
    Crafting = 1,
    Wielding = 2,
    SpellCasting = 4,
    OpeningTrade = 8,
    DroppingWeenie = 16,
    GivingWeenie = 32,
    LoggingOut = 64,
    MovingWeenie = 128,
    UsingWeenie = 256,
    MovingWeenieToContainer = 512,
    TeleToLifestone = 1024,
    Portalling = 2048,
    Animating = 4096,
    ChangeCombatMode = 8192,
    TeleToMarketplace = 16384,
    TeleToPKArena = 32768,
    AdvocateTeleport = 65536,
    TeleToHouse = 131072,
    TeleToMansion = 262144,
    TeleToAllegianceHometown = 524288,
    RecallCommand = 1033216,
}

---CastEventType.
---@enum CastEventType
CastEventType = {
    Untargetted = 0,
    Targetted = 1,
}

---ChatChannel.
---@enum ChatChannel
ChatChannel = {
    None = 0,
    Allegiance = 1,
    General = 2,
    Trade = 3,
    LFG = 4,
    Roleplay = 5,
    Olthoi = 6,
    Society = 7,
    CelestialHand = 8,
    EldrytchWeb = 9,
    RadiantBlood = 10,
}

---ClientCapability.
---@enum ClientCapability
ClientCapability = {
    ImGui = 1,
}

---ClientConfirmationType.
---@enum ClientConfirmationType
ClientConfirmationType = {
    UNDEF = 0,
    ALLEGIANCE_SWEAR = 1,
    ALTER_SKILL = 2,
    ALTER_ATTRIBUTE = 3,
    FELLOWSHIP_RECRUIT = 4,
    CRAFT_INTERACTION = 5,
    USE_AUGMENTATION = 6,
    YESNO = 7,
}

---DistanceType.
---@enum DistanceType
DistanceType = {
    T2D = 0,
    T3D = 1,
}

---FellowshipEventType.
---@enum FellowshipEventType
FellowshipEventType = {
    Create = 0,
    Quit = 1,
    Dismiss = 2,
    Recruit = 3,
    Disband = 4,
}

---ScriptStartupType.
---@enum ScriptStartupType
ScriptStartupType = {
    Manual = 0,
    Global = 1,
    Account = 2,
    Character = 3,
}

---UseActionType.
---@enum UseActionType
UseActionType = {
    Default = 0,
    Vendor = 1,
    SummonPortal = 2,
    Portal = 3,
}

---ChatType under UtilityBelt's name.
ChatMessageType = ChatType

---UB's ClientState names; the values are the texts game.State returns.
---@enum ClientState
ClientState = {
    Initial = "Initial",
    Game_Started = "Game_Started",
    Character_Select_Screen = "CharacterSelect",
    Creating_Character = "Creating_Character",
    Entering_Game = "Entering_Game",
    PlayerDesc_Received = "PlayerDesc_Received",
    In_Game = "InGame",
    Logging_Out = "LoggingOut",
    Disconnected = "Disconnected",
}

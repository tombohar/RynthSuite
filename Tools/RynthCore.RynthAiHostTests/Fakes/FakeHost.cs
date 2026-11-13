using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using RynthCore.PluginSdk;

namespace RynthCore.RynthAiHostTests.Fakes;

/// <summary>
/// An in-process stand-in for the engine. The real RynthCoreHost is a struct of native function
/// pointers; here each pointer is an [UnmanagedCallersOnly] static method that answers from the
/// static state below, so tested code gets the answers a test sets up and nothing reaches a game.
///
/// Deliberately NOT faked:
///   * GetPlayerId, unless a test asks for it with Create(playerId): ExpressionEngine's getgame*
///     functions read raw client memory (0x008379A8) once GetPlayerId returns non-zero, which
///     would crash this process. With no GetPlayerId they return the calendar for a zero clock,
///     which the tests pin. Buff tests (no expressions) use the opt-in.
///   * GetContainerContents: WorldObjectCache then builds its inventory from objects it already
///     knows (see <see cref="MakeCache"/>).
///   * Most action calls (combat mode, ...) stay no-ops. The nav, buff and weapon calls are
///     recorded: SetAutoRun, TurnToHeading (the fake turns instantly), JumpNonAutonomous,
///     CastSpell, UseObject and RequestId.
///
/// State is static because function pointers cannot capture; call <see cref="Reset"/> first.
/// </summary>
internal static unsafe class FakeHost
{
    // ── State a test sets up ──────────────────────────────────────────────────
    public static readonly Dictionary<uint, string> Names = new();
    public static readonly Dictionary<uint, (uint Cell, float X, float Y, float Z)> Positions = new();
    public static readonly Dictionary<uint, uint> ItemTypes = new();
    public static readonly Dictionary<(uint Id, uint Key), int> Ints = new();
    public static readonly Dictionary<(uint Id, uint Key), double> Doubles = new();
    public static readonly Dictionary<(uint Id, uint Key), long> Quads = new();
    public static readonly Dictionary<(uint Id, uint Key), bool> Bools = new();
    public static readonly Dictionary<(uint Id, uint Key), string> Strings = new();
    public static readonly Dictionary<(uint Id, uint Skill), (int Buffed, int Training)> Skills = new();
    public static readonly Dictionary<(uint Id, uint Skill, int Raw), int> SkillLevels = new();
    public static readonly Dictionary<(uint Id, uint Attr, int Raw), uint> Attributes = new();
    public static readonly Dictionary<uint, uint[]> SpellIds = new();
    /// <summary>Data id properties (STypeDID): (object, key) -> value, e.g. a scroll's Spell 28.</summary>
    public static readonly Dictionary<(uint Id, uint Key), uint> DataIds = new();
    /// <summary>Instance id properties (STypeIID): (object, key) -> object id, e.g. a pet's PetOwner 44.
    /// When it has entries at Create() time the host also gets GetObjectInstanceIdProperty (opt-in).</summary>
    public static readonly Dictionary<(uint Id, uint Key), uint> InstanceIds = new();
    public static readonly Dictionary<uint, uint[]> Palettes = new();
    public static readonly HashSet<uint> KnownSpells = new();
    public static readonly HashSet<uint> NotAttackable = new();
    public static readonly List<(uint SpellId, double Expiry)> Enchantments = new();
    /// <summary>Item id -> container id. When it has entries at Create() time the host also gets
    /// GetObjectOwnershipInfo (opt-in, so tests written without it see no ownership, as before).</summary>
    public static readonly Dictionary<uint, uint> Containers = new();
    /// <summary>Extra ids GetContainerContents lists for a container without them being in it (the
    /// client lists what the player wields in the player's own contents; ownership says no container).</summary>
    public static readonly Dictionary<uint, List<uint>> ContentsExtra = new();
    /// <summary>Opt-in (set before Create): v87 GetWieldedItems answering <see cref="Equipped"/> (the
    /// engine's EquipmentTracker: item -> EquipMask). <see cref="EquippedNotReady"/> answers -1.</summary>
    public static bool EquippedCalls;
    public static bool EquippedNotReady;
    /// <summary>Opt-in (set before Create): v93 GetMonsterHealth answering <see cref="PlateHealth"/> (the
    /// engine monster plates' last server reading: id -> ratio, age in ms). <see cref="PlateHealthVersion"/>
    /// non-zero sets the table's Version (e.g. 92: the slot is there but the engine is too old).
    /// Calls are counted in <see cref="PlateHealthQueries"/>.</summary>
    public static bool PlateHealthCalls;
    public static uint PlateHealthVersion;
    public static readonly Dictionary<uint, (float Ratio, uint AgeMs)> PlateHealth = new();
    public static int PlateHealthQueries;
    public static readonly Dictionary<uint, uint> Equipped = new();
    /// <summary>Item id -> the creature wielding it (GetObjectOwnershipInfo's wielder). Entries here
    /// also install GetObjectOwnershipInfo; an id in neither map reads as unknown ownership.</summary>
    public static readonly Dictionary<uint, uint> Wielders = new();
    /// <summary>Object id -> PublicWeenieDesc bitfield (0x2000 corpse, 0x8 player, ...). When it has
    /// entries at Create() time the host also gets GetObjectBitfield (opt-in, as Containers).</summary>
    public static readonly Dictionary<uint, uint> Bitfields = new();
    /// <summary>The api struct the last Create() built (a test attaching a plugin needs it).</summary>
    public static RynthCoreApiNative LastApi;

    public static double ServerTime;
    public static float Vitae = 1.0f;
    public static bool Portaling;
    public static (uint Cell, float X, float Y, float Z) PlayerPose;
    public static bool HasPose = true;
    public static (uint Hp, uint MaxHp, uint Stam, uint MaxStam, uint Mana, uint MaxMana) Vitals;
    public static (uint Hp, uint Stam, uint Mana) BaseVitals;
    public static string WorldName = "";
    public static string AccountName = "";
    /// <summary>A test's extra engine calls (e.g. FakeServerState), set into the table last by Create; Reset clears it.</summary>
    public delegate void ApiHook(ref RynthCoreApiNative api);
    public static ApiHook? ConfigureApi;
    /// <summary>The player's facing, 0 = north, clockwise (read back through the pose quaternion).</summary>
    public static double HeadingDeg;
    public static readonly Dictionary<uint, uint> Wcids = new();
    /// <summary>The character's spellbook as ReadKnownSpells returns it (empty = snapshot cold).</summary>
    public static readonly List<uint> Spellbook = new();
    /// <summary>Only used when Create(playerId: ...) installs GetPlayerId (see the class note).</summary>
    public static uint PlayerIdValue;

    // ── What tested code sent to the engine ───────────────────────────────────
    public static readonly List<string> Logs = new();
    public static readonly List<string> Chat = new();
    /// <summary>The chat type (colour) of each <see cref="Chat"/> line, in the same order.</summary>
    public static readonly List<int> ChatTypes = new();
    public static readonly List<string> ChatCommands = new();
    /// <summary>Every SetAutoRun call, in order; AutoRun is the last value sent.</summary>
    public static readonly List<bool> AutoRunCalls = new();
    public static bool AutoRun;
    public static int Jumps;
    /// <summary>Runs inside CastSpell, after the cast is recorded (a UseDone that lands during the send).</summary>
    public static Action? AfterCastSpell;
    /// <summary>Opt-in: wires TapJump (the keyboard jump, 0xF61B); calls land in <see cref="TapJumps"/>.</summary>
    public static bool TapJumpCalls;
    public static int TapJumps;
    /// <summary>Jumps and item moves in the order they were called ("tapjump", "jump", "move:XXXXXXXX").</summary>
    public static readonly List<string> Order = new();
    /// <summary>TapJump returns 0 (the client refused the jump); the call still counts in TapJumps.</summary>
    public static bool TapJumpRefuse;
    public static int Turns;
    public static readonly List<(uint Target, int SpellId)> Casts = new();
    /// <summary>True: CastSpell returns false (the client refused it; nothing is recorded in <see cref="Casts"/>).</summary>
    public static bool CastRefused;
    /// <summary>CastSpell calls refused while <see cref="CastRefused"/> was set.</summary>
    public static int CastsRefused;
    /// <summary>Opt-in: wires GiveObjectTo (AC's give action); every call lands in <see cref="Gives"/>.</summary>
    public static bool GiveCalls;
    /// <summary>Every GiveObjectTo (item, target, amount; 0 = the whole object), in order.</summary>
    public static readonly List<(uint Item, uint Target, int Amount)> Gives = new();
    /// <summary>When true (the default) a give lands at once, as the server does when it isn't busy:
    /// the item goes to the target. False = the server hasn't taken it (still carried).</summary>
    public static bool GiveLands = true;
    /// <summary>Optional: runs for every give instead of the default landing (a test's own server: NPC takes one per give, busy, ...).</summary>
    public static Action<uint, uint, int>? GiveHandler;
    /// <summary>Opt-in: wires StopCompletely (off by default so no other test sees it).</summary>
    public static bool StopCalls;
    /// <summary>StopCompletely calls (opt-in with <see cref="StopCalls"/>).</summary>
    public static int Stops;
    /// <summary>Opt-in: wires MeleeAttack and MissileAttack; calls land in <see cref="AttackCalls"/>.</summary>
    public static bool AttackSendCalls;
    /// <summary>Every melee or missile attack (kind, target, height, power), in order (opt-in with <see cref="AttackSendCalls"/>).</summary>
    public static readonly List<(string Kind, uint Target, int Height, float Power)> AttackCalls = new();
    /// <summary>Opt-in (set before Create): wires NativeAttack and SelectItem; a native attack lands in
    /// <see cref="AttackCalls"/> as ("native", the selected item, height, power).</summary>
    public static bool NativeAttackCalls;
    /// <summary>Opt-in (set before Create): wires CancelAttack; calls are counted in <see cref="CancelAttacks"/>.</summary>
    public static bool CancelAttackCalls;
    public static int CancelAttacks;
    /// <summary>Opt-in: wires SetMotion; calls land in <see cref="MotionCalls"/>.</summary>
    public static bool SetMotionCalls;
    /// <summary>Every SetMotion (motion, on), in order (opt-in with <see cref="SetMotionCalls"/>).</summary>
    public static readonly List<(uint Motion, bool On)> MotionCalls = new();
    /// <summary>Movement and cast calls in the order they reached the engine ("autorun on/off", "stop",
    /// "motion 0x.. on/off", "cast N").</summary>
    public static readonly List<string> Sent = new();
    /// <summary>Every UseObject (a wield, a use) and RequestId (an identify), in order.</summary>
    public static readonly List<uint> Uses = new();
    public static readonly List<uint> IdRequests = new();
    /// <summary>Opt-in (set before Create): QueryHealth (counted in <see cref="HealthQueries"/>) and GetLastIdTime (answering <see cref="IdTimes"/>, 0 = never).</summary>
    public static bool TargetCalls;
    public static readonly List<uint> HealthQueries = new();
    public static readonly Dictionary<uint, long> IdTimes = new();
    /// <summary>Every MoveItemInternal (an item into a pack), in order (opt-in with <see cref="WeaponCalls"/>).</summary>
    public static readonly List<(uint Id, uint Pack, int Amount)> Moves = new();
    /// <summary>Objects the fake says have appraisal data (HasAppraisalData).</summary>
    public static readonly HashSet<uint> Appraised = new();
    /// <summary>False = RequestId refuses (as the engine does around a teleport or a death): returns 0
    /// and is counted in <see cref="RefusedIdRequests"/>, not in <see cref="IdRequests"/>.</summary>
    public static bool RequestIdAccepts = true;
    public static int RefusedIdRequests;
    /// <summary>False = UseObject returns 0 (the engine did not send it); the use is still recorded in <see cref="Uses"/>.</summary>
    public static bool UseObjectAccepts = true;
    /// <summary>Opt-in (set before Create): UseObject, RequestId, HasAppraisalData and
    /// GetCurrentCombatMode (answering <see cref="CombatModeValue"/>). Tests written without
    /// them keep seeing those calls as absent.</summary>
    public static bool WeaponCalls;
    /// <summary>Opt-in GetContainerContents answering from <see cref="Containers"/> (item -> container),
    /// in insertion order. Off by default: see the class note.</summary>
    public static bool ContentsCalls;
    /// <summary>The engine's VTank signal (API v74 GetVTankState flags: 1 = watching, 2 = running);
    /// -1 = not installed (an engine before v74). Opt-in: set before Create().</summary>
    public static int VTankFlags = -1;
    /// <summary>Opt-in (set before Create): the server gates combat paces casts on, GetUseDoneSeq
    /// (answering <see cref="UseDoneSeq"/>) and GetCastBusyState (answering <see cref="CastBusy"/>).</summary>
    public static bool ServerGates;
    /// <summary>The count of server UseDone (0x01C7) events the fake reports.</summary>
    public static int UseDoneSeq;
    /// <summary>The error code of the last UseDone (GetLastUseDone, v70; 0 = completed, 0x1D = too busy).
    /// Opt-in with <see cref="LastUseDoneCalls"/> (set before Create, together with ServerGates).</summary>
    public static uint LastUseDoneError;
    public static bool LastUseDoneCalls;
    /// <summary>One more UseDone with <paramref name="error"/> (0 = the action completed).</summary>
    public static void UseDone(uint error) { UseDoneSeq++; LastUseDoneError = error; }
    /// <summary>1 while a cast gesture animates (GetCastBusyState), else 0.</summary>
    public static int CastBusy;
    /// <summary>Opt-in (set before Create): GetLastWeenieError, answering the fields below.</summary>
    public static bool WeenieErrorCalls;
    public static int WeenieErrSeq;
    public static uint WeenieErrCode, WeenieErrEvent, WeenieErrObject;
    /// <summary>The server refused something: 0x028A WeenieError, or 0x00A0 InventoryServerSaveFailed (obj = the item).</summary>
    public static void WeenieError(uint code, uint evt = 0x028A, uint obj = 0)
    { WeenieErrSeq++; WeenieErrCode = code; WeenieErrEvent = evt; WeenieErrObject = obj; }
    public static int VTankSeq;
    public static int CombatModeValue = 1;
    /// <summary>Opt-in (set before Create): ChangeCombatMode, recorded in <see cref="CombatModeRequests"/>.</summary>
    public static bool CombatModeCalls;
    public static readonly List<int> CombatModeRequests = new();
    /// <summary>True: ChangeCombatMode returns 0 (the engine refused, e.g. around a teleport) and records nothing in <see cref="CombatModeRequests"/>.</summary>
    public static bool CombatModeRefuse;
    /// <summary>ChangeCombatMode calls refused while <see cref="CombatModeRefuse"/> was set.</summary>
    public static int CombatModeRefusals;
    /// <summary>Opt-in (set before Create): WieldItem (a wield into a named slot, API v70+). The
    /// host then reports API version 73, so only tests that ask for it see the newer calls.</summary>
    public static bool WieldCalls;
    /// <summary>Opt-in (set before Create): v80 PutItemInContainer (the raw move packet), recorded in <see cref="Puts"/>.</summary>
    public static bool PutCalls;
    /// <summary>Every PutItemInContainer (item, container, placement), in order (opt-in with <see cref="PutCalls"/>).</summary>
    public static readonly List<(uint Id, uint Container, int Placement)> Puts = new();
    /// <summary>Every WieldItem (item, EquipMask slot), in order (opt-in with <see cref="WieldCalls"/>).</summary>
    public static readonly List<(uint Id, uint Mask)> Wields = new();
    /// <summary>Opt-in (set before Create): UseObject, MoveItemExternal, the salvage panel
    /// (open / add / execute) and GetObjectWielderInfo (answering from <see cref="Wielded"/>).</summary>
    public static bool SalvageCalls;
    /// <summary>Every MoveItemExternal (item, target container, amount), in order (opt-in with <see cref="SalvageCalls"/>).</summary>
    public static readonly List<(uint Id, uint Target, int Amount)> ExternalMoves = new();
    /// <summary>Every SalvagePanelAddItem, in order.</summary>
    public static readonly List<uint> SalvageAdds = new();
    /// <summary>The items added since the last execute, each SalvagePanelExecute, in order.</summary>
    public static readonly List<List<uint>> SalvageExecutes = new();
    /// <summary>Items SalvagePanelAddItem turns down (the panel refuses them).</summary>
    public static readonly HashSet<uint> RefuseSalvageAdd = new();
    /// <summary>Called on SalvagePanelExecute with what was added since the last one (the test
    /// removes the salvaged items from its world here).</summary>
    public static Action<List<uint>>? OnSalvageExecute;
    /// <summary>Item id -> wielded location: GetObjectWielderInfo says the player wields it there.</summary>
    public static readonly Dictionary<uint, uint> Wielded = new();
    /// <summary>Opt-in (set before Create): SalvageDirect (API v82, salvage without the panel).</summary>
    public static bool SalvageDirectCalls;
    /// <summary>Every SalvageDirect (UST, ids), in order (opt-in with <see cref="SalvageDirectCalls"/>).</summary>
    public static readonly List<(uint Tool, List<uint> Ids)> SalvageDirects = new();
    /// <summary>What SalvageDirect answers; null = the count of ids (sent). -1 = sender not bound.</summary>
    public static int? SalvageDirectAnswer;
    /// <summary>Called on a sent SalvageDirect with (UST, ids) (the test plays the server here).</summary>
    public static Action<uint, List<uint>>? OnSalvageDirect;
    private static readonly List<uint> _panelItems = new();
    /// <summary>Opt-in (set before Create): CloseContainer (API v76; the host then reports at
    /// least v76) and GetGroundContainerId (answering <see cref="GroundContainer"/>).</summary>
    public static bool CloseContainerCalls;
    /// <summary>Every CloseContainer (container id), in order (opt-in with <see cref="CloseContainerCalls"/>).</summary>
    public static readonly List<uint> Closes = new();
    /// <summary>The container the client has open (GetGroundContainerId), 0 = none.</summary>
    public static uint GroundContainer;
    /// <summary>The item selected in the game (GetSelectedItemId); 0 = none.</summary>
    public static uint SelectedItem;
    /// <summary>Opt-in (set before Create): UseObjectOn (an item used on another, e.g. a spirit on a
    /// pet essence); every call lands in <see cref="UsesOn"/>.</summary>
    public static bool UseOnCalls;
    /// <summary>Every UseObjectOn (source, target), in order (opt-in with <see cref="UseOnCalls"/>).</summary>
    public static readonly List<(uint Source, uint Target)> UsesOn = new();
    /// <summary>Opt-in (set before Create): the trade window and vendor calls (API v72 / v67). A vendor named
    /// <see cref="VendorName"/> (id 0x70000001) is open; every trade action and vendor transaction is recorded.</summary>
    public static bool TradeVendorCalls;
    public static string VendorName = "Town Vendor";
    /// <summary>Trade actions in order: "accept", "decline", "reset", "close", "add:<id>".</summary>
    public static readonly List<string> TradeActions = new();
    /// <summary>Every VendorSell: the vendor id and the item ids.</summary>
    public static readonly List<(uint Vendor, uint[] Items)> VendorSells = new();
    /// <summary>Every VendorBuy: the vendor id and the (object, amount) entries.</summary>
    public static readonly List<(uint Vendor, (uint Id, int Amount)[] Entries)> VendorBuys = new();
    /// <summary>Opt-in: install SendPluginCommand (the engine's plugin-command broker, API v64).</summary>
    public static bool PluginCommandCalls;
    /// <summary>What SendPluginCommand answers: true = delivered (the target plugin is loaded).</summary>
    public static bool PluginCommandDelivered = true;
    /// <summary>When set, answers each SendPluginCommand (plugin, action, value) instead of <see cref="PluginCommandDelivered"/>.</summary>
    public static Func<string, string, string, bool>? PluginCommandAnswer;
    /// <summary>Every SendPluginCommand (plugin, action, value), in order.</summary>
    public static readonly List<(string Plugin, string Action, string Value)> PluginCommands = new();
    /// <summary>For each <see cref="PluginCommands"/> entry, how many chat lines were written before it (call order).</summary>
    public static readonly List<int> PluginCommandChatCounts = new();
    /// <summary>Opt-in (set before Create): ForceResetBusyCount (counted in <see cref="ForceResets"/>)
    /// and GetBusyState (answering <see cref="BusyStateValue"/>).</summary>
    public static bool BusyCalls;
    public static int ForceResets;
    public static int BusyStateValue;

    /// <summary>Opt-in (set before Create): GetObjectProperties (API v84), answering the record
    /// <see cref="PropertyRecord"/> returns for an id (null = the engine knows nothing: 0).</summary>
    public static bool ObjectPropertiesCalls;
    public static Func<uint, byte[]?>? PropertyRecord;
    /// <summary>GetObjectProperties calls, in order (the buffer-size retries included).</summary>
    public static readonly List<uint> PropertyQueries = new();

    /// <summary>Object id -> CPhysicsObj m_state (GetObjectState; 0x4 ETHEREAL = a door that is open).
    /// When it has entries at Create() time the host also gets GetObjectState (opt-in, as Bitfields).</summary>
    public static readonly Dictionary<uint, uint> ObjectStates = new();
    /// <summary>Runs inside UseObject, after the use is recorded (the test plays the server here).</summary>
    public static Action<uint>? OnUseObject;
    /// <summary>Runs inside UseObjectOn, after it is recorded.</summary>
    public static Action<uint, uint>? OnUseObjectOn;
    /// <summary>Object id -> (health, max health) for GetTargetVitals (opt-in: installed when it has entries at Create()).</summary>
    public static readonly Dictionary<uint, (uint Hp, uint MaxHp)> TargetVitals = new();
    /// <summary>Opt-in (set before Create): GetLiveObjectIds (API v69) lists every id in <see cref="Names"/>.</summary>
    public static bool LiveObjectsCalls;

    // ANSI strings handed back to tested code stay alive for the whole run.
    private static readonly Dictionary<string, IntPtr> _ansi = new();

    /// <summary>Opt-in: install the Nav3D calls (clear, ring, line, triangle) and record them.</summary>
    public static bool Nav3DCalls;
    public static readonly List<(float X, float Y, float Z, float Radius, uint Color)> Nav3DRings = new();
    public static readonly List<(float X1, float Y1, float Z1, float X2, float Y2, float Z2, float Thick, uint Color)> Nav3DLines = new();
    public static readonly List<(float X1, float Y1, float Z1, float X2, float Y2, float Z2, float X3, float Y3, float Z3, uint Color)> Nav3DTriangles = new();

    public static void Reset()
    {
        EquippedCalls = false; EquippedNotReady = false; Equipped.Clear();
        PlateHealthCalls = false; PlateHealthVersion = 0; PlateHealth.Clear(); PlateHealthQueries = 0;
#if !RYNTHILT_TESTS
        RynthCore.Plugin.RynthAi.EquippedState.Invalidate();
#endif
        Nav3DCalls = false; Nav3DRings.Clear(); Nav3DLines.Clear(); Nav3DTriangles.Clear();
        Names.Clear(); Positions.Clear(); ItemTypes.Clear();
        Ints.Clear(); Doubles.Clear(); Quads.Clear(); Bools.Clear(); Strings.Clear();
        Skills.Clear(); SkillLevels.Clear(); Attributes.Clear();
        SpellIds.Clear(); DataIds.Clear(); InstanceIds.Clear(); Palettes.Clear(); KnownSpells.Clear(); NotAttackable.Clear(); Enchantments.Clear();
        Containers.Clear(); ContentsExtra.Clear(); Wielders.Clear(); Bitfields.Clear();
        ServerTime = 0; Vitae = 1.0f; Portaling = false; PlayerPose = default; HasPose = true;
        Vitals = default; BaseVitals = default; WorldName = ""; AccountName = "";
        HeadingDeg = 0; Wcids.Clear(); Spellbook.Clear(); PlayerIdValue = 0;
        Logs.Clear(); Chat.Clear(); ChatTypes.Clear(); ChatCommands.Clear(); AfterCastSpell = null;
        AutoRunCalls.Clear(); AutoRun = false; Jumps = 0; TapJumpCalls = false; TapJumps = 0; TapJumpRefuse = false; Order.Clear(); Turns = 0; Casts.Clear(); CastRefused = false; CastsRefused = 0; StopCalls = false; Stops = 0; SetMotionCalls = false; MotionCalls.Clear(); AttackSendCalls = false; AttackCalls.Clear(); NativeAttackCalls = false; CancelAttackCalls = false; CancelAttacks = 0; Sent.Clear(); GiveCalls = false; Gives.Clear(); GiveLands = true; GiveHandler = null;
        Uses.Clear(); IdRequests.Clear(); TargetCalls = false; HealthQueries.Clear(); IdTimes.Clear(); Moves.Clear(); Appraised.Clear(); WeaponCalls = false; ContentsCalls = false; CombatModeValue = 1;
        RequestIdAccepts = true; RefusedIdRequests = 0; UseObjectAccepts = true;
        VTankFlags = -1; VTankSeq = 0; LogCostMicros = 0;
        ServerGates = false; UseDoneSeq = 0; CastBusy = 0; LastUseDoneError = 0; LastUseDoneCalls = false;
        WeenieErrorCalls = false; WeenieErrSeq = 0; WeenieErrCode = WeenieErrEvent = WeenieErrObject = 0;
#if !RYNTHILT_TESTS
        // Spell mana costs "unknown" (no reserve beyond Get Mana At, no cost gate), so a portal.dat on
        // the PC doesn't change results; the mana reserve tests set their own costs.
        RynthCore.Plugin.RynthAi.BuffManager.ManaCostOverride = _ => 0;
#endif
        WieldCalls = false; Wields.Clear(); PutCalls = false; Puts.Clear();
        SalvageCalls = false; ExternalMoves.Clear(); SalvageAdds.Clear(); SalvageExecutes.Clear();
        RefuseSalvageAdd.Clear(); OnSalvageExecute = null; Wielded.Clear(); _panelItems.Clear();
        SalvageDirectCalls = false; SalvageDirects.Clear(); SalvageDirectAnswer = null; OnSalvageDirect = null;
        CloseContainerCalls = false; Closes.Clear(); GroundContainer = 0;
        SelectedItem = 0;
        UseOnCalls = false; UsesOn.Clear();
        PluginCommandCalls = false; PluginCommandDelivered = true; PluginCommandAnswer = null; PluginCommands.Clear(); PluginCommandChatCounts.Clear();
        TradeVendorCalls = false; TradeActions.Clear(); VendorSells.Clear(); VendorBuys.Clear();
        CombatModeCalls = false; CombatModeRequests.Clear(); CombatModeRefuse = false; CombatModeRefusals = 0;
        BusyCalls = false; ForceResets = 0; BusyStateValue = 0;
        ObjectPropertiesCalls = false; PropertyRecord = null; PropertyQueries.Clear();
        ObjectStates.Clear(); OnUseObject = null; OnUseObjectOn = null;
        TargetVitals.Clear(); LiveObjectsCalls = false;
        ConfigureApi = null;
    }

    /// <summary>
    /// Puts the player at nav coordinates (ns, ew) on the landscape: the landblock cell and
    /// local x/y that NavCoordinateHelper converts back to the same coordinates.
    /// </summary>
    public static void SetNavPosition(double ns, double ew, float z = 0f)
    {
        PlayerPose = (NavCell(ns, ew, out float x, out float y), x, y, z);
    }

    /// <summary>The outdoor cell id holding (ns, ew), and the local x/y in it.</summary>
    public static uint NavCell(double ns, double ew, out float x, out float y)
    {
        double gx = (ew * 10.0 + 1019.5) * 24.0, gy = (ns * 10.0 + 1019.5) * 24.0;
        uint lbX = (uint)(gx / 192.0), lbY = (uint)(gy / 192.0);
        x = (float)(gx - lbX * 192.0);
        y = (float)(gy - lbY * 192.0);
        uint cell = (uint)(x / 24f) * 8 + (uint)(y / 24f) + 1;   // outdoor cells 0x0001-0x0040
        return (lbX << 24) | (lbY << 16) | cell;
    }

    /// <summary>
    /// A host whose faked calls answer from the state above. <paramref name="playerId"/> non-zero
    /// also installs GetPlayerId; never do that for a test that runs expressions (see the class note).
    /// </summary>
    public static RynthCoreHost Create(uint playerId = 0)
    {
        var api = new RynthCoreApiNative { Version = RynthCoreHost.CurrentApiVersion };
        PlayerIdValue = playerId;
        if (playerId != 0)
            api.GetPlayerIdFn       = (IntPtr)(delegate* unmanaged[Cdecl]<uint>)&GetPlayerId;
        if (Containers.Count > 0 || Wielders.Count > 0)
            api.GetObjectOwnershipInfoFn = (IntPtr)(delegate* unmanaged[Cdecl]<uint, uint*, uint*, uint*, int>)&GetObjectOwnershipInfo;
        if (EquippedCalls)
            api.GetWieldedItemsFn = (IntPtr)(delegate* unmanaged[Cdecl]<uint*, uint*, int, int>)&GetWieldedItems;
        if (ContentsCalls)
            api.GetContainerContentsFn = (IntPtr)(delegate* unmanaged[Cdecl]<uint, uint*, int, int>)&GetContainerContents;
        if (Bitfields.Count > 0)
            api.GetObjectBitfieldFn = (IntPtr)(delegate* unmanaged[Cdecl]<uint, uint>)&GetObjectBitfield;
        if (ObjectStates.Count > 0)
            api.GetObjectStateFn = (IntPtr)(delegate* unmanaged[Cdecl]<uint, uint*, int>)&GetObjectState;
        if (InstanceIds.Count > 0)
            api.GetObjectInstanceIdPropertyFn = (IntPtr)(delegate* unmanaged[Cdecl]<uint, uint, uint*, int>)&GetObjectInstanceIdProperty;
        api.SetAutoRunFn            = (IntPtr)(delegate* unmanaged[Cdecl]<int, int>)&SetAutoRun;
        api.TurnToHeadingFn         = (IntPtr)(delegate* unmanaged[Cdecl]<float, int>)&TurnToHeading;
        api.GetPlayerHeadingFn      = (IntPtr)(delegate* unmanaged[Cdecl]<float*, int>)&GetPlayerHeading;
        api.JumpNonAutonomousFn     = (IntPtr)(delegate* unmanaged[Cdecl]<float, int>)&JumpNonAutonomous;
        if (TapJumpCalls)
            api.TapJumpFn           = (IntPtr)(delegate* unmanaged[Cdecl]<int>)&TapJump;
        api.CastSpellFn             = (IntPtr)(delegate* unmanaged[Cdecl]<uint, int, int>)&CastSpell;
        if (StopCalls)
            api.StopCompletelyFn    = (IntPtr)(delegate* unmanaged[Cdecl]<int>)&StopCompletely;
        if (SetMotionCalls)
            api.SetMotionFn         = (IntPtr)(delegate* unmanaged[Cdecl]<uint, int, int>)&SetMotion;
        if (AttackSendCalls)
        {
            api.MeleeAttackFn       = (IntPtr)(delegate* unmanaged[Cdecl]<uint, int, float, int>)&MeleeAttack;
            api.MissileAttackFn     = (IntPtr)(delegate* unmanaged[Cdecl]<uint, int, float, int>)&MissileAttack;
        }
        if (NativeAttackCalls)
        {
            api.NativeAttackFn      = (IntPtr)(delegate* unmanaged[Cdecl]<int, float, int>)&NativeAttack;
            api.SelectItemFn        = (IntPtr)(delegate* unmanaged[Cdecl]<uint, int>)&SelectItem;
        }
        if (Nav3DCalls)
        {
            api.Nav3DClearFn      = (IntPtr)(delegate* unmanaged[Cdecl]<void>)&Nav3DClear;
            api.Nav3DAddRingFn     = (IntPtr)(delegate* unmanaged[Cdecl]<float, float, float, float, float, uint, void>)&Nav3DAddRing;
            api.Nav3DAddLineFn     = (IntPtr)(delegate* unmanaged[Cdecl]<float, float, float, float, float, float, float, uint, void>)&Nav3DAddLine;
            api.Nav3DAddTriangleFn = (IntPtr)(delegate* unmanaged[Cdecl]<float, float, float, float, float, float, float, float, float, uint, void>)&Nav3DAddTriangle;
        }
        if (GiveCalls)
            api.GiveObjectToFn      = (IntPtr)(delegate* unmanaged[Cdecl]<uint, uint, int, int>)&GiveObjectTo;
        if (WeaponCalls)
        {
            api.UseObjectFn            = (IntPtr)(delegate* unmanaged[Cdecl]<uint, int>)&UseObject;
            api.RequestIdFn            = (IntPtr)(delegate* unmanaged[Cdecl]<uint, int>)&RequestId;
            api.HasAppraisalDataFn     = (IntPtr)(delegate* unmanaged[Cdecl]<uint, int>)&HasAppraisalData;
            api.GetCurrentCombatModeFn = (IntPtr)(delegate* unmanaged[Cdecl]<int>)&GetCurrentCombatMode;
            api.MoveItemInternalFn     = (IntPtr)(delegate* unmanaged[Cdecl]<uint, uint, int, int, int>)&MoveItemInternal;
        }
        if (TargetCalls)
        {
            api.QueryHealthFn   = (IntPtr)(delegate* unmanaged[Cdecl]<uint, int>)&QueryHealth;
            api.GetLastIdTimeFn = (IntPtr)(delegate* unmanaged[Cdecl]<uint, long>)&GetLastIdTime;
        }
        if (SalvageCalls)
        {
            api.UseObjectFn            = (IntPtr)(delegate* unmanaged[Cdecl]<uint, int>)&UseObject;
            api.MoveItemExternalFn     = (IntPtr)(delegate* unmanaged[Cdecl]<uint, uint, int, int>)&MoveItemExternal;
            api.SalvagePanelOpenFn     = (IntPtr)(delegate* unmanaged[Cdecl]<uint, int>)&SalvagePanelOpen;
            api.SalvagePanelAddItemFn  = (IntPtr)(delegate* unmanaged[Cdecl]<uint, int>)&SalvagePanelAddItem;
            api.SalvagePanelExecuteFn  = (IntPtr)(delegate* unmanaged[Cdecl]<int>)&SalvagePanelExecute;
            api.GetObjectWielderInfoFn = (IntPtr)(delegate* unmanaged[Cdecl]<uint, uint*, uint*, int>)&GetObjectWielderInfo;
        }
        if (SalvageDirectCalls)
        {
            api.Version = Math.Max(api.Version, 82u);
            api.SalvageDirectFn = (IntPtr)(delegate* unmanaged[Cdecl]<uint, uint*, int, int>)&SalvageDirect;
        }
        if (CombatModeCalls)
            api.ChangeCombatModeFn  = (IntPtr)(delegate* unmanaged[Cdecl]<int, int>)&ChangeCombatMode;
        if (CancelAttackCalls)
            api.CancelAttackFn      = (IntPtr)(delegate* unmanaged[Cdecl]<int>)&CancelAttack;
        if (TargetVitals.Count > 0)
            api.GetTargetVitalsFn   = (IntPtr)(delegate* unmanaged[Cdecl]<uint, uint*, uint*, uint*, uint*, uint*, uint*, int>)&GetTargetVitals;
        if (LiveObjectsCalls)
        {
            api.Version = Math.Max(api.Version, 69u);
            api.GetLiveObjectIdsFn  = (IntPtr)(delegate* unmanaged[Cdecl]<uint*, int, int>)&GetLiveObjectIds;
        }
        if (BusyCalls)
        {
            api.ForceResetBusyCountFn = (IntPtr)(delegate* unmanaged[Cdecl]<void>)&ForceResetBusyCount;
            api.GetBusyStateFn        = (IntPtr)(delegate* unmanaged[Cdecl]<int>)&GetBusyState;
        }
        if (UseOnCalls)
            api.UseObjectOnFn       = (IntPtr)(delegate* unmanaged[Cdecl]<uint, uint, int>)&UseObjectOn;
        if (TradeVendorCalls)
        {
            api.Version = Math.Max(api.Version, 82u);
            api.GetVendorInfoFn        = (IntPtr)(delegate* unmanaged[Cdecl]<VendorInfoNative*, int>)&GetVendorInfo;
            api.GetVendorItemsFn       = (IntPtr)(delegate* unmanaged[Cdecl]<VendorItemNative*, int, int>)&GetVendorItems;
            api.VendorBuyFn            = (IntPtr)(delegate* unmanaged[Cdecl]<uint, VendorTradeEntryNative*, int, uint>)&VendorBuy;
            api.VendorSellFn           = (IntPtr)(delegate* unmanaged[Cdecl]<uint, uint*, int, uint>)&VendorSell;
            api.GetVendorTradeStatusFn = (IntPtr)(delegate* unmanaged[Cdecl]<VendorTradeStatusNative*, int>)&GetVendorTradeStatus;
            api.GetTradeStateFn   = (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, int>)&GetTradeState;
            api.GetTradeItemsFn   = (IntPtr)(delegate* unmanaged[Cdecl]<int, uint*, int, int>)&GetTradeItems;
            api.TradeOpenFn       = (IntPtr)(delegate* unmanaged[Cdecl]<uint, int>)&TradeOpen;
            api.TradeAddFn        = (IntPtr)(delegate* unmanaged[Cdecl]<uint, uint, int>)&TradeAdd;
            api.TradeAcceptFn     = (IntPtr)(delegate* unmanaged[Cdecl]<int>)&TradeAccept;
            api.TradeDeclineFn    = (IntPtr)(delegate* unmanaged[Cdecl]<int>)&TradeDecline;
            api.TradeResetFn      = (IntPtr)(delegate* unmanaged[Cdecl]<int>)&TradeReset;
            api.TradeCloseFn      = (IntPtr)(delegate* unmanaged[Cdecl]<int>)&TradeClose;
        }
        if (WieldCalls)
        {
            api.Version     = 73;
            api.WieldItemFn = (IntPtr)(delegate* unmanaged[Cdecl]<uint, uint, int>)&WieldItem;
        }
        if (PutCalls)
        {
            api.Version = Math.Max(api.Version, 80u);
            api.PutItemInContainerFn = (IntPtr)(delegate* unmanaged[Cdecl]<uint, uint, int, int>)&PutItemInContainer;
        }
        api.GetObjectWcidFn         = (IntPtr)(delegate* unmanaged[Cdecl]<uint, uint>)&GetObjectWcid;
        api.GetSelectedItemIdFn     = (IntPtr)(delegate* unmanaged[Cdecl]<uint>)&GetSelectedItemId;
        api.ReadKnownSpellsFn       = (IntPtr)(delegate* unmanaged[Cdecl]<uint*, int, int>)&ReadKnownSpells;
        api.LogFn                   = (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, void>)&Log;
        api.WriteToChatFn           = (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, int, int>)&WriteToChat;
        api.InvokeChatParserFn      = (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, int>)&InvokeChatParser;
        api.GetObjectNameFn         = (IntPtr)(delegate* unmanaged[Cdecl]<uint, IntPtr>)&GetObjectName;
        api.GetObjectPositionFn     = (IntPtr)(delegate* unmanaged[Cdecl]<uint, uint*, float*, float*, float*, int>)&GetObjectPosition;
        api.GetItemTypeFn           = (IntPtr)(delegate* unmanaged[Cdecl]<uint, uint*, int>)&GetItemType;
        api.ObjectIsAttackableFn    = (IntPtr)(delegate* unmanaged[Cdecl]<uint, int>)&ObjectIsAttackable;
        api.GetObjectIntPropertyFn  = (IntPtr)(delegate* unmanaged[Cdecl]<uint, uint, int*, int>)&GetObjectIntProperty;
        api.GetObjectDoublePropertyFn = (IntPtr)(delegate* unmanaged[Cdecl]<uint, uint, double*, int>)&GetObjectDoubleProperty;
        api.GetObjectQuadPropertyFn = (IntPtr)(delegate* unmanaged[Cdecl]<uint, uint, long*, int>)&GetObjectQuadProperty;
        api.GetObjectBoolPropertyFn = (IntPtr)(delegate* unmanaged[Cdecl]<uint, uint, int*, int>)&GetObjectBoolProperty;
        api.GetObjectStringPropertyFn = (IntPtr)(delegate* unmanaged[Cdecl]<uint, uint, IntPtr>)&GetObjectStringProperty;
        api.GetObjectSkillFn        = (IntPtr)(delegate* unmanaged[Cdecl]<uint, uint, int*, int*, int>)&GetObjectSkill;
        api.GetObjectSkillBuffedFn  = (IntPtr)(delegate* unmanaged[Cdecl]<uint, uint, int, int*, int>)&GetObjectSkillLevel;
        api.GetObjectAttributeFn    = (IntPtr)(delegate* unmanaged[Cdecl]<uint, uint, int, uint*, int>)&GetObjectAttribute;
        api.GetObjectSpellIdsFn     = (IntPtr)(delegate* unmanaged[Cdecl]<uint, uint*, int, int>)&GetObjectSpellIds;
        api.GetObjectDataIdPropertyFn = (IntPtr)(delegate* unmanaged[Cdecl]<uint, uint, uint*, int>)&GetObjectDataIdProperty;
        api.GetObjectPalettesFn     = (IntPtr)(delegate* unmanaged[Cdecl]<uint, uint*, uint*, int, int>)&GetObjectPalettes;
        api.IsSpellKnownFn          = (IntPtr)(delegate* unmanaged[Cdecl]<uint, uint, int>)&IsSpellKnown;
        api.ReadPlayerEnchantmentsFn = (IntPtr)(delegate* unmanaged[Cdecl]<uint*, double*, int, int>)&ReadPlayerEnchantments;
        api.GetServerTimeFn         = (IntPtr)(delegate* unmanaged[Cdecl]<double>)&GetServerTime;
        api.GetVitaeFn              = (IntPtr)(delegate* unmanaged[Cdecl]<uint, float>)&GetVitae;
        api.IsPortalingFn           = (IntPtr)(delegate* unmanaged[Cdecl]<int>)&IsPortaling;
        api.GetPlayerPoseFn         = (IntPtr)(delegate* unmanaged[Cdecl]<uint*, float*, float*, float*, float*, float*, float*, float*, int>)&GetPlayerPose;
        api.GetPlayerVitalsFn       = (IntPtr)(delegate* unmanaged[Cdecl]<uint*, uint*, uint*, uint*, uint*, uint*, int>)&GetPlayerVitals;
        api.GetPlayerBaseVitalsFn   = (IntPtr)(delegate* unmanaged[Cdecl]<uint*, uint*, uint*, int>)&GetPlayerBaseVitals;
        api.GetWorldNameFn          = (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr>)&GetWorldName;
        api.GetAccountNameFn        = (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr>)&GetAccountName;
        if (ServerGates)
        {
            api.GetUseDoneSeqFn    = (IntPtr)(delegate* unmanaged[Cdecl]<int>)&GetUseDoneSeq;
            api.GetCastBusyStateFn = (IntPtr)(delegate* unmanaged[Cdecl]<int>)&GetCastBusyState;
            if (LastUseDoneCalls)
                api.GetLastUseDoneFn = (IntPtr)(delegate* unmanaged[Cdecl]<int*, uint*, int>)&GetLastUseDone;
        }
        if (WeenieErrorCalls)
            api.GetLastWeenieErrorFn = (IntPtr)(delegate* unmanaged[Cdecl]<int*, uint*, uint*, uint*, int>)&GetLastWeenieError;
        if (VTankFlags >= 0)
        {
            api.Version = Math.Max(api.Version, 74u);
            api.GetVTankStateFn = (IntPtr)(delegate* unmanaged[Cdecl]<int*, int>)&GetVTankState;
        }
        if (CloseContainerCalls)
        {
            api.Version = Math.Max(api.Version, 76u);
            api.CloseContainerFn       = (IntPtr)(delegate* unmanaged[Cdecl]<uint, int>)&CloseContainer;
            api.GetGroundContainerIdFn = (IntPtr)(delegate* unmanaged[Cdecl]<uint>)&GetGroundContainerId;
        }
        if (PluginCommandCalls)
        {
            api.Version = Math.Max(api.Version, 64u);
            api.SendPluginCommandFn = (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, int>)&SendPluginCommand;
        }
        if (ObjectPropertiesCalls)
        {
            api.Version = Math.Max(api.Version, 84u);
            api.GetObjectPropertiesFn = (IntPtr)(delegate* unmanaged[Cdecl]<uint, byte*, int, int>)&GetObjectProperties;
        }
        ConfigureApi?.Invoke(ref api);
        LastApi = api;
        if (EquippedCalls) api.Version = Math.Max(api.Version, 87u);   // v87 GetWieldedItems
        if (PlateHealthCalls)
        {
            api.GetMonsterHealthFn = (IntPtr)(delegate* unmanaged[Cdecl]<uint, float*, uint*, int>)&GetMonsterHealth;
            api.Version = PlateHealthVersion != 0 ? PlateHealthVersion : Math.Max(api.Version, 93u);   // v93
        }
        return new RynthCoreHost(api);
    }

#if !RYNTHILT_TESTS
    /// <summary>
    /// A WorldObjectCache that knows the given objects: each id must have a name in
    /// <see cref="Names"/>. Ids with a <see cref="Positions"/> entry land on the landscape
    /// (a creature when <see cref="ItemTypes"/> has the creature flag 0x10), the rest in the
    /// inventory. Health ratios make creatures count as alive for the meta monster conditions.
    /// </summary>
    public static RynthCore.Plugin.RynthAi.WorldObjectCache MakeCache(RynthCoreHost host, uint playerId,
        IEnumerable<uint> ids, IReadOnlyDictionary<uint, float>? health = null)
    {
        var cache = new RynthCore.Plugin.RynthAi.WorldObjectCache(host);
        cache.SetPlayerId(playerId);
        foreach (uint id in ids)
        {
            _ = cache[unchecked((int)id)];   // the indexer's lazy lookup classifies and files it
            if (health != null && health.TryGetValue(id, out float hr))
                cache.OnUpdateHealth(id, hr);
        }
        return cache;
    }
#endif

    private static IntPtr Ansi(string s)
    {
        if (!_ansi.TryGetValue(s, out IntPtr p))
            _ansi[s] = p = Marshal.StringToHGlobalAnsi(s);
        return p;
    }

    // ── The fakes ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Microseconds each Log call costs, spun on the calling thread (0 = free). The engine's real
    /// log sink opens, appends to and closes the log file for every line, on the caller's thread:
    /// about 350 us a line measured on the PC the clients run on (2026-10-02), so the flood
    /// benchmark sets this to make plugin log volume cost what it costs in game.
    /// </summary>
    public static int LogCostMicros;

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void Log(IntPtr msg)
    {
        Logs.Add(Marshal.PtrToStringAnsi(msg) ?? "");
        if (LogCostMicros > 0)
        {
            long until = System.Diagnostics.Stopwatch.GetTimestamp()
                         + LogCostMicros * System.Diagnostics.Stopwatch.Frequency / 1_000_000;
            while (System.Diagnostics.Stopwatch.GetTimestamp() < until) { }
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int GetUseDoneSeq() => UseDoneSeq;

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int GetCastBusyState() => CastBusy;

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int GetLastWeenieError(int* seq, uint* error, uint* eventType, uint* objectId)
    {
        *seq = WeenieErrSeq; *error = WeenieErrCode; *eventType = WeenieErrEvent; *objectId = WeenieErrObject;
        return 1;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int GetLastUseDone(int* seq, uint* error)
    {
        *seq = UseDoneSeq;
        *error = UseDoneSeq == 0 ? 0 : LastUseDoneError;
        return 1;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int WriteToChat(IntPtr text, int type) { Chat.Add(Marshal.PtrToStringUni(text) ?? ""); ChatTypes.Add(type); return 1; }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int SendPluginCommand(IntPtr plugin, IntPtr action, IntPtr value)
    {
        PluginCommands.Add((Marshal.PtrToStringAnsi(plugin) ?? "", Marshal.PtrToStringAnsi(action) ?? "", Marshal.PtrToStringAnsi(value) ?? ""));
        PluginCommandChatCounts.Add(Chat.Count);
        var (p, a, v) = PluginCommands[^1];
        return (PluginCommandAnswer?.Invoke(p, a, v) ?? PluginCommandDelivered) ? 1 : 0;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int InvokeChatParser(IntPtr text) { ChatCommands.Add(Marshal.PtrToStringUni(text) ?? ""); return 1; }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static IntPtr GetObjectName(uint id) => Names.TryGetValue(id, out string? n) ? Ansi(n) : IntPtr.Zero;

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int GetObjectPosition(uint id, uint* cell, float* x, float* y, float* z)
    {
        if (!Positions.TryGetValue(id, out var p)) return 0;
        *cell = p.Cell; *x = p.X; *y = p.Y; *z = p.Z;
        return 1;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int GetItemType(uint id, uint* flags)
    {
        if (!ItemTypes.TryGetValue(id, out uint f)) return 0;
        *flags = f;
        return 1;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int GetObjectOwnershipInfo(uint id, uint* container, uint* wielder, uint* location)
    {
        bool inPack = Containers.TryGetValue(id, out uint c);
        bool worn = Wielders.TryGetValue(id, out uint w);
        if (!inPack && !worn) return 0;
        *container = c;
        *wielder = w;
        *location = 0;
        return 1;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int GetMonsterHealth(uint id, float* ratio, uint* ageMs)
    {
        PlateHealthQueries++;
        bool ok = PlateHealth.TryGetValue(id, out var h);
        if (ratio != null) *ratio = ok ? h.Ratio : -1f;
        if (ageMs != null) *ageMs = ok ? h.AgeMs : uint.MaxValue;
        return ok ? 1 : 0;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int GetWieldedItems(uint* ids, uint* locations, int max)
    {
        if (EquippedNotReady) return -1;
        int n = 0;
        foreach (var kv in Equipped)
        {
            if (n >= max) break;
            ids[n] = kv.Key; locations[n] = kv.Value; n++;
        }
        return n;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int GetContainerContents(uint container, uint* output, int max)
    {
        int n = 0;
        foreach (var kv in Containers)
            if (kv.Value == container && n < max) output[n++] = kv.Key;
        if (ContentsExtra.TryGetValue(container, out var extra))
            foreach (uint id in extra)
                if (n < max) output[n++] = id;
        return n;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static uint GetObjectBitfield(uint id) => Bitfields.TryGetValue(id, out uint b) ? b : 0u;

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int MoveItemInternal(uint id, uint pack, int slot, int amount) { Moves.Add((id, pack, amount)); return 1; }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int ObjectIsAttackable(uint id) => NotAttackable.Contains(id) ? 0 : 1;

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int GetTargetVitals(uint id, uint* hp, uint* maxHp, uint* stam, uint* maxStam, uint* mana, uint* maxMana)
    {
        if (!TargetVitals.TryGetValue(id, out var v)) return 0;
        *hp = v.Hp; *maxHp = v.MaxHp; *stam = 0; *maxStam = 0; *mana = 0; *maxMana = 0;
        return 1;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int GetLiveObjectIds(uint* buffer, int capacity)
    {
        int n = 0;
        foreach (uint id in Names.Keys)
        {
            if (n < capacity) buffer[n] = id;
            n++;
        }
        return n;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int GetObjectIntProperty(uint id, uint key, int* v)
    {
        if (!Ints.TryGetValue((id, key), out int x)) return 0;
        *v = x;
        return 1;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int GetObjectDoubleProperty(uint id, uint key, double* v)
    {
        if (!Doubles.TryGetValue((id, key), out double x)) return 0;
        *v = x;
        return 1;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int GetObjectQuadProperty(uint id, uint key, long* v)
    {
        if (!Quads.TryGetValue((id, key), out long x)) return 0;
        *v = x;
        return 1;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int GetObjectBoolProperty(uint id, uint key, int* v)
    {
        if (!Bools.TryGetValue((id, key), out bool x)) return 0;
        *v = x ? 1 : 0;
        return 1;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static IntPtr GetObjectStringProperty(uint id, uint key)
        => Strings.TryGetValue((id, key), out string? s) ? Ansi(s) : IntPtr.Zero;

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int GetObjectSkill(uint id, uint skill, int* buffed, int* training)
    {
        if (!Skills.TryGetValue((id, skill), out var s)) return 0;
        *buffed = s.Buffed; *training = s.Training;
        return 1;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int GetObjectSkillLevel(uint id, uint skill, int raw, int* level)
    {
        if (!SkillLevels.TryGetValue((id, skill, raw), out int l)) return 0;
        *level = l;
        return 1;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int GetObjectAttribute(uint id, uint attr, int raw, uint* value)
    {
        if (!Attributes.TryGetValue((id, attr, raw), out uint v)) return 0;
        *value = v;
        return 1;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int GetObjectInstanceIdProperty(uint id, uint key, uint* v)
    {
        if (!InstanceIds.TryGetValue((id, key), out uint x)) return 0;
        *v = x;
        return 1;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int GetObjectDataIdProperty(uint id, uint key, uint* v)
    {
        if (!DataIds.TryGetValue((id, key), out uint x)) return 0;
        *v = x;
        return 1;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int GetObjectSpellIds(uint id, uint* buf, int max)
    {
        if (!SpellIds.TryGetValue(id, out uint[]? ids)) return -1;
        for (int i = 0; i < ids.Length && i < max; i++) buf[i] = ids[i];
        return ids.Length;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int GetObjectProperties(uint id, byte* buf, int size)
    {
        PropertyQueries.Add(id);
        byte[]? rec = PropertyRecord?.Invoke(id);
        if (rec == null) return 0;
        if (rec.Length <= size) rec.AsSpan().CopyTo(new Span<byte>(buf, size));
        return rec.Length;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int GetObjectPalettes(uint id, uint* subIds, uint* offsets, int max)
    {
        if (!Palettes.TryGetValue(id, out uint[]? p)) return -1;
        for (int i = 0; i < p.Length && i < max; i++) { subIds[i] = p[i]; offsets[i] = 0; }
        return p.Length;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int IsSpellKnown(uint id, uint spell) => KnownSpells.Contains(spell) ? 1 : 0;

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int ReadPlayerEnchantments(uint* ids, double* expiry, int max)
    {
        int n = Math.Min(Enchantments.Count, max);
        for (int i = 0; i < n; i++) { ids[i] = Enchantments[i].SpellId; expiry[i] = Enchantments[i].Expiry; }
        return Enchantments.Count;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static double GetServerTime() => ServerTime;

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static float GetVitae(uint id) => Vitae;

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int IsPortaling() => Portaling ? 1 : 0;

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int GetVTankState(int* sequence)
    {
        if (sequence != null) *sequence = VTankSeq;
        return VTankFlags < 0 ? 0 : VTankFlags;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int GetPlayerPose(uint* cell, float* x, float* y, float* z, float* qw, float* qx, float* qy, float* qz)
    {
        if (!HasPose) return 0;
        *cell = PlayerPose.Cell; *x = PlayerPose.X; *y = PlayerPose.Y; *z = PlayerPose.Z;
        // Heading h (0 = north, clockwise) is a physics yaw of -h about z.
        double half = -HeadingDeg * Math.PI / 360.0;
        *qw = (float)Math.Cos(half); *qx = 0; *qy = 0; *qz = (float)Math.Sin(half);
        return 1;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static uint GetPlayerId() => PlayerIdValue;

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void Nav3DClear() { Nav3DRings.Clear(); Nav3DLines.Clear(); Nav3DTriangles.Clear(); }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void Nav3DAddRing(float x, float y, float z, float radius, float thick, uint color) => Nav3DRings.Add((x, y, z, radius, color));

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void Nav3DAddLine(float x1, float y1, float z1, float x2, float y2, float z2, float thick, uint color)
        => Nav3DLines.Add((x1, y1, z1, x2, y2, z2, thick, color));

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void Nav3DAddTriangle(float x1, float y1, float z1, float x2, float y2, float z2, float x3, float y3, float z3, uint color)
        => Nav3DTriangles.Add((x1, y1, z1, x2, y2, z2, x3, y3, z3, color));

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static uint GetSelectedItemId() => SelectedItem;

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int SetAutoRun(int on) { AutoRun = on != 0; AutoRunCalls.Add(AutoRun); Sent.Add(AutoRun ? "autorun on" : "autorun off"); return 1; }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int CancelAttack() { CancelAttacks++; return 1; }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int MeleeAttack(uint target, int height, float power) { AttackCalls.Add(("melee", target, height, power)); return 1; }
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int NativeAttack(int height, float power) { AttackCalls.Add(("native", SelectedItem, height, power)); return 1; }
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int SelectItem(uint id) { SelectedItem = id; return 1; }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int MissileAttack(uint target, int height, float power) { AttackCalls.Add(("missile", target, height, power)); return 1; }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int SetMotion(uint motion, int on) { MotionCalls.Add((motion, on != 0)); Sent.Add($"motion 0x{motion:X8} {(on != 0 ? "on" : "off")}"); return 1; }

    // The fake character turns instantly.
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int TurnToHeading(float deg) { HeadingDeg = deg; Turns++; return 1; }

    // What the engine's GetPlayerHeading really returns: 0 = East, counter-clockwise, i.e.
    // 90 - the compass heading (Freya 2026-10-20: quaternion 13.9, GetPlayerHeading 76.1).
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int GetPlayerHeading(float* deg)
    {
        if (!HasPose) return 0;
        *deg = (float)(((90.0 - HeadingDeg) % 360.0 + 360.0) % 360.0);
        return 1;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int JumpNonAutonomous(float extent) { Jumps++; Order.Add("jump"); return 1; }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int TapJump() { TapJumps++; Order.Add("tapjump"); return TapJumpRefuse ? 0 : 1; }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int CastSpell(uint target, int spellId) { if (CastRefused) { CastsRefused++; return 0; } Casts.Add((target, spellId)); Sent.Add($"cast {spellId}"); AfterCastSpell?.Invoke(); return 1; }
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int StopCompletely() { Stops++; Sent.Add("stop"); return 1; }
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int GetVendorInfo(VendorInfoNative* info)
    {
        *info = default;
        info->VendorId = 0x70000001;
        byte[] name = System.Text.Encoding.ASCII.GetBytes(VendorName);
        for (int i = 0; i < name.Length && i < 63; i++) info->Name[i] = name[i];
        return 1;
    }
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int GetVendorItems(VendorItemNative* items, int max) => 0;
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static uint VendorBuy(uint vendor, VendorTradeEntryNative* entries, int count)
    {
        var list = new (uint, int)[count];
        for (int i = 0; i < count; i++) list[i] = (entries[i].ObjectId, entries[i].Amount);
        VendorBuys.Add((vendor, list));
        return 1;
    }
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static uint VendorSell(uint vendor, uint* ids, int count)
    {
        var list = new uint[count];
        for (int i = 0; i < count; i++) list[i] = ids[i];
        VendorSells.Add((vendor, list));
        return 1;
    }
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int GetVendorTradeStatus(VendorTradeStatusNative* status) => 0;
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int GetTradeState(IntPtr state) => 0;
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int GetTradeItems(int side, uint* buffer, int capacity) => 0;
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int TradeOpen(uint target) { TradeActions.Add("open:" + target.ToString("X8")); return 1; }
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int TradeAdd(uint item, uint slot) { TradeActions.Add("add:" + item.ToString("X8")); return 1; }
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int TradeAccept() { TradeActions.Add("accept"); return 1; }
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int TradeDecline() { TradeActions.Add("decline"); return 1; }
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int TradeReset() { TradeActions.Add("reset"); return 1; }
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int TradeClose() { TradeActions.Add("close"); return 1; }
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void ForceResetBusyCount() { ForceResets++; BusyStateValue = 0; }
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int GetBusyState() => BusyStateValue;
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int GiveObjectTo(uint item, uint target, int amount)
    {
        Gives.Add((item, target, amount));
        if (GiveHandler != null) { GiveHandler(item, target, amount); return 1; }
        // The server takes the whole object for amount 0, or for an amount that covers the stack (a single
        // item has no StackCount: its stack is 1, so an explicit 1 takes it whole).
        if (GiveLands && (amount == 0 || amount >= (Ints.TryGetValue((item, 12), out int stack) ? stack : 1))) Containers[item] = target;
        return 1;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int GetCurrentCombatMode() => CombatModeValue;
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int ChangeCombatMode(int mode) { if (CombatModeRefuse) { CombatModeRefusals++; return 0; } CombatModeRequests.Add(mode); return 1; }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int UseObject(uint id) { Uses.Add(id); OnUseObject?.Invoke(id); return UseObjectAccepts ? 1 : 0; }
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int GetObjectState(uint id, uint* state)
    {
        if (!ObjectStates.TryGetValue(id, out uint s)) return 0;
        *state = s;
        return 1;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int UseObjectOn(uint source, uint target) { UsesOn.Add((source, target)); OnUseObjectOn?.Invoke(source, target); return 1; }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int WieldItem(uint id, uint mask) { Wields.Add((id, mask)); return 1; }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int PutItemInContainer(uint id, uint container, int placement) { Puts.Add((id, container, placement)); return 1; }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int CloseContainer(uint id) { Closes.Add(id); return 1; }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static uint GetGroundContainerId() => GroundContainer;

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int MoveItemExternal(uint id, uint target, int amount) { ExternalMoves.Add((id, target, amount)); Order.Add($"move:{id:X8}"); return 1; }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int SalvagePanelOpen(uint tool) => 1;

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int SalvagePanelAddItem(uint id)
    {
        SalvageAdds.Add(id);
        if (RefuseSalvageAdd.Contains(id)) return 0;
        _panelItems.Add(id);
        return 1;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int SalvagePanelExecute()
    {
        var items = new List<uint>(_panelItems);
        _panelItems.Clear();
        SalvageExecutes.Add(items);
        OnSalvageExecute?.Invoke(items);
        return 1;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int SalvageDirect(uint tool, uint* ids, int count)
    {
        var list = new List<uint>();
        for (int i = 0; i < count; i++) list.Add(ids[i]);
        SalvageDirects.Add((tool, list));
        int answer = SalvageDirectAnswer ?? count;
        if (answer > 0) OnSalvageDirect?.Invoke(tool, list);
        return answer;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int GetObjectWielderInfo(uint id, uint* wielder, uint* location)
    {
        if (!Wielded.TryGetValue(id, out uint loc)) return 0;
        *wielder = PlayerIdValue;
        *location = loc;
        return 1;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int RequestId(uint id)
    {
        if (!RequestIdAccepts) { RefusedIdRequests++; return 0; }
        IdRequests.Add(id);
        return 1;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int QueryHealth(uint id) { HealthQueries.Add(id); return 1; }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static long GetLastIdTime(uint id) => IdTimes.TryGetValue(id, out long t) ? t : 0L;

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int HasAppraisalData(uint id) => Appraised.Contains(id) ? 1 : 0;

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static uint GetObjectWcid(uint id) => Wcids.TryGetValue(id, out uint w) ? w : 0;

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int ReadKnownSpells(uint* buf, int max)
    {
        for (int i = 0; i < Spellbook.Count && i < max; i++) buf[i] = Spellbook[i];
        return Spellbook.Count;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int GetPlayerVitals(uint* hp, uint* maxHp, uint* stam, uint* maxStam, uint* mana, uint* maxMana)
    {
        *hp = Vitals.Hp; *maxHp = Vitals.MaxHp; *stam = Vitals.Stam; *maxStam = Vitals.MaxStam;
        *mana = Vitals.Mana; *maxMana = Vitals.MaxMana;
        return 1;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int GetPlayerBaseVitals(uint* hp, uint* stam, uint* mana)
    {
        *hp = BaseVitals.Hp; *stam = BaseVitals.Stam; *mana = BaseVitals.Mana;
        return 1;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static IntPtr GetWorldName() => WorldName.Length > 0 ? Ansi(WorldName) : IntPtr.Zero;

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static IntPtr GetAccountName() => AccountName.Length > 0 ? Ansi(AccountName) : IntPtr.Zero;
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace RynthCore.Plugin.RynthAi.LegacyUi;

public static class DashWindows
{
    public static bool ShowMacroRules;
    public static bool ShowMonsters;
    public static bool ShowNavigation;
    public static bool ShowWeapons;
    public static bool ShowLua;
    public static bool ShowDungeonMap;
}

public sealed class LegacyUiSettings
{
    // ── Runtime-only state — never persisted ────────────────────────────────
    [JsonIgnore] public bool IsMacroRunning;
    /// <summary>
    /// RynthSort's auto-sort is on (read from RynthSort every couple of seconds): Auto Cram stands down and
    /// its checkboxes are greyed out, as the two fought over the packs (owner, 2026-11-03). Never saved.
    /// </summary>
    [JsonIgnore] public bool AutoSortOn;
    /// <summary>Meta state — set only by meta actions (SetState, CallState, ReturnFromCall, UI, meta load).</summary>
    [JsonIgnore] public string CurrentState = "Default";
    /// <summary>Bot action state — what the bot is doing right now (Default, Combat, Looting, Navigating, Buffing).
    /// Separate from meta state so operational cycling doesn't corrupt meta rule matching.</summary>
    [JsonIgnore] public string BotAction = "Default";
    /// <summary>Display only: what the "Buffing" slot is doing (Healing, Restoring mana, Restoring
    /// stamina, Buffing). Written by RynthAiPlugin every tick; never read by control flow.
    /// Displays use ActivityArbiter.DisplayLabel, never this field alone.</summary>
    [JsonIgnore] public string BuffingLabel = "Buffing";
    [JsonIgnore] public bool IsRecordingNav;

    public bool EnableBuffing = true;
    public bool EnableCombat;
    public bool EnableNavigation;
    public bool EnableLooting;
    public bool EnableMeta;
    public bool EnableRaycasting = true;
    public bool MetaDebug;

    /// <summary>
    /// "Allow metas to send keystrokes (/mt send)". Off by default (2026-10-19). Mag-Tools' /mt send,
    /// click, movement, strafe, client minimize|restore and the key fallback of /mt jump post raw
    /// key and mouse messages to the game window; with this off they log once and do nothing.
    /// Deliberately not in the expression settings map, /mt opt or the phone's settings: a meta
    /// must not be able to switch it on for itself.
    /// </summary>
    public bool AllowMetaKeystrokes;

    public int MovementMode;
    public float NavStopTurnAngle = 20f;
    public float NavResumeTurnAngle = 10f;
    public float NavDeadZone = 4f;
    public float NavSweepMult = 2.5f;
    public float NavLookaheadYards = 4.0f;       // most the aim point is blended toward the next waypoint (0 = off); the nav point reach scales it down (NavigationEngine.CornerLookaheadYards)
    public float NavShortcutYards = 1.0f;        // on arrival, skip waypoints only while the straight run to a later one stays within this of every skipped point (0 = visit every point)
    public float NavTurnRateDegPerSec = 270f;    // mode 0 (heading servo) max turn rate
    public float NavTier1TurnSpeed = 3.0f;       // mode 1 (CM_Movement) DoMovement turn-command speed (magnitude of CMotionInterp turn_speed; 1.0 = native keyboard rate)
    public float PostPortalDelaySec = 4.0f;
    // Route recovery (NavigationEngine detours): when stuck after the jump fails, or when nav
    // itself wanders off the route, plan a way back with the dungeon map / RynthNav navmesh.
    public bool  NavRecoveryEnabled = true;
    public float NavOffTrackYards = 170f;        // off track beyond this (never below 160: metas pull the bot up to ~159 yd away)
    public int   NavMaxDetourAttempts = 3;       // detours per waypoint before falling back to the escape ladder alone
    // Dungeon patrol (/ra dunnav-patrol, Settings > Navigation > Dungeon Patrol; DungeonPatrolOptions).
    // The defaults are the patrol as it was before these settings (2026-10-12). A running patrol
    // is rebuilt when one changes (RynthAiCommands.TickDunPatrolReroute).
    public int   DunPatrolCoverage;              // PatrolCoverage: 0 whole dungeon, 1 loops only, 2 everything, 3 sweep
    public int   DunPatrolSkipDeadEnds = 10;     // whole dungeon: dead-end areas of up to this many cells are skipped (0 = none)
    public bool  DunPatrolAvoidFloors = true;    // lava/acid floor cells (HazardTextures.txt) kept out of the patrol
    public float DunPatrolWallDistance = 1.0f;   // metres every patrol point keeps from walls where there is room
    public float DunPatrolLookInSize = 10.0f;    // rooms this big (m) the patrol only skirts get a walk to their middle
    public float DunPatrolSightYd = 20.0f;       // sweep: how far a monster notices you, so how far apart the lanes may be
    // Outdoor hunt (/ra hunt, Settings > Navigation > Outdoor Hunt; OutdoorHunt, 2026-10-23): walk from monster to
    // monster around obstacles on RynthNav's map, into buildings through their doors (2026-10-25).
    // Hunt Range 192 by default since 2026-10-25: the radar's default range, so the hunt covers the radar.
    public float OutdoorHuntRange = (float)OutdoorHunt.DefaultRangeYd;   // yd from the character: how far the next monster may be
    public float OutdoorHuntRadius = 0f;         // yd from where the hunt started that its picks stay within (0 = anywhere)
    // What the next monster is chosen by (2026-11-08, OutdoorHunt.PickMode): 0 the nearest (the hunt as
    // it was), 1 the biggest pack, 2 the pack worth the most experience (learned XP per kill).
    public int OutdoorHuntPick = 0;
    // On: macro start, loading a Circular or Linear route, and nav taking over again after a
    // pause that moved the character (combat, looting, buffing, doors) all head for the closest
    // waypoint on the route. Off: a pause carries on to the waypoint it was heading for, a macro
    // start keeps the last waypoint, and a loaded route starts at its first point. Hazard escapes
    // and corpse runs always rejoin at the closest waypoint.
    public bool  NavResumeNearest = true;
    public float T2Speed = 1.0f;
    public float T2DistanceTo = 0.5f;
    public float T2ReissueMs = 2000f;
    public float T2MaxRangeYd = 500f;
    public int T2MaxLandblocks = 3;
    public float T2WalkWithinYd = 5f;

    public string CurrentNavPath = string.Empty;
    public string CurrentLootPath = string.Empty;
    public string CurrentMetaPath = string.Empty;
    /// <summary>The meta's rules here differ from its file: the last save of an edit failed
    /// (MetaSaver). At login this profile's copy of the rules is then used instead of the file's,
    /// so the edit isn't lost; the next successful save clears it.</summary>
    public bool MetaUnsaved;

    public int MacroSettingsIdx = 1;
    public int NavProfileIdx = 1;
    public int LootProfileIdx;
    public int MetaProfileIdx = 1;

    /// <summary>Buff profile buffing uses (BuffProfiles\*.json); empty = the built-in buff list.
    /// Chosen under Loaded Files (Buffs) or with "Use for buffing" in the Spells window.</summary>
    public string CurrentBuffProfilePath = string.Empty;
    /// <summary>Index into the Buffs file list (0 = "Built-in").</summary>
    public int BuffProfileIdx;
    /// <summary>The Spells window (/ra spells) is open.</summary>
    public bool ShowSpellsWindow;

    public bool EnableAutostack = true;
    public bool EnableAutocram = true;
    // VTank's ReadUnknownScrolls (default on): loot scrolls of spells the character doesn't
    // know and can learn (school trained or specialized, ACE's skill rule), then read them
    // at a safe moment; scrolls already in the pack too. Needs looting on for corpses.
    public bool ReadUnknownScrolls = true;
    // Unbox Aetheria When Idle (a player's request, 2026-11-01; default off): with the macro on and
    // nothing to do, an Aetheria Mana Stone is used on each Coalesced Aetheria in the pack, one at a
    // time, to reveal its sigil (Loot/AetheriaUnboxer.cs). The stone isn't used up on ACE.
    public bool UnboxAetheriaWhenIdle = false;
    public bool EnableCombineSalvage = true;
    public bool CombineBagsDuringSalvage = true;
    // Salvage (and combine bags) by sending the 0x027D salvage request straight to the server, no
    // salvage panel on screen (engine API v82 SalvageDirect; default on, the panel when unsupported).
    public bool SalvageWithoutPanel = true;

    public bool ShowTargetStaminaMana;

    /// <summary>Mag-style item info (/ra iteminfo): on-select, per-field visibility, chat type, prefix.
    /// Edited in the "RynthAi Item Info" window.</summary>
    public RynthCore.Plugin.RynthAi.ItemInfo.MagItemInfoSettings ItemInfoSettings = new();

    public bool EnableMissileCrafting = true;
    public int MissileCraftAmmoThreshold = 1000;

    /// <summary>Not read by the corpse looting (pickups are paced by the server's confirmations);
    /// kept so saved profiles round-trip. Hidden in Advanced Settings.</summary>
    public int LootInterItemDelayMs = 50;
    // Loot timer defaults are the measured baseline (LootBaseline.cs).
    public int LootContentSettleMs = LootBaseline.ContentSettleMs;
    public int LootEmptyCorpseMs = LootBaseline.EmptyCorpseMs;
    /// <summary>Not read by the corpse looting (a close is timed by the deferred-close logic); kept
    /// so saved profiles round-trip. Hidden in Advanced Settings.</summary>
    public int LootClosingDelayMs = 200;
    public int LootAssessWindowMs = LootBaseline.AssessWindowMs;
    public int LootRetryTimeoutMs = LootBaseline.RetryTimeoutMs;
    public int LootOpenRetryMs = LootBaseline.OpenRetryMs;
    public int LootCorpseTimeoutMs = LootBaseline.CorpseTimeoutMs;
    public bool LootJumpEnabled = false;
    /// <summary>
    /// Pickup with macro off (2026-11-02, on by default): a chest the player opens is looted by the
    /// loaded loot profile even with the macro or Looting off (like /ra lootthis). With both on the
    /// macro's own chest looting takes it.
    /// </summary>
    public bool LootChestsMacroOff = true;
    /// <summary>
    /// Pickup with macro off > Leave the chest open (2026-11-02, off by default: the chest is closed when done, as before): when the pickup is done the
    /// chest stays open and chat says so, instead of RynthAi closing it (a chest that locks itself on close
    /// can't be opened again without its key or quest item).
    /// </summary>
    public bool LootChestsMacroOffLeaveOpen = false;
    /// <summary>
    /// Item Tool > Chest re-lock wait (s) (2026-11-05): the chest looter's next key goes on the chest
    /// this long after it was opened (sooner when the client reads it locked again). 125 s covers ACE's
    /// default chest reset (120 s); a chest with a shorter timer can use less. 5..600
    /// (ItemTool.ChestKeyLooter.ClampRelockWaitSec).
    /// </summary>
    public int ChestRelockWaitSec = ItemTool.ChestKeyLooter.DefaultRelockWaitSec;
    public int LootJumpHeight = 10; // kept for old profiles and metas; jump-loot uses the keyboard tap jump (fixed height)

    public int SalvageOpenDelayFirstMs = 400;
    public int SalvageOpenDelayFastMs = 50;
    public int SalvageAddDelayFirstMs = 600;
    public int SalvageAddDelayFastMs = 50;
    public int SalvageSalvageDelayMs = 50;
    public int SalvageResultDelayFirstMs = 1000;
    public int SalvageResultDelayFastMs = 250;
    public bool UseDispelItems;
    public bool CastDispelSelf;
    public bool AutoFellowMgmt;
    /// <summary>
    /// Auto Fellowship Management > Open fellowship on create (2026-11-04, on by default, as VTank's
    /// auto fellow management does): a fellowship RynthAi creates (/mt fellow create, a meta's
    /// /fellowship create) is opened once it exists. Acts only with AutoFellowMgmt on; a fellowship
    /// made or closed by hand is never touched.
    /// </summary>
    public bool OpenFellowOnCreate = true;
    public bool MChargesWhenOff;

    public int HealAt = 60;
    // Vitals consumables and safety stops (2026-09-28). Stops are options: most
    // metas handle a death themselves (run back), so the macro keeps going unless asked.
    public bool UsePotions = true;
    // Unlimited buff gems (Asheron's Benediction, Blackmoor's Favor) and field rations (2026-09-29).
    public bool UseBuffItems = true;
    public int MakeRationsBelow = 5;   // 0 = don't cook field rations
    public bool UseKitsInMagicMode = true;
    public bool PeaceModeForKits = false;
    // Use a healing kit only when its success chance (ACE's own skill check) is at
    // least this %, else cast the spell instead, as VTank does. 0 = always use kits.
    public int KitMinSuccessPct = 70;
    // The same for stamina kits (2026-11-03, its own setting): below it Revitalize Self (or a
    // stamina potion) goes instead. 0 = always use stamina kits.
    public int StaminaKitMinSuccessPct = 70;
    // Emergency heal: at or under this health %, a healing kit (out of Magic mode, ahead of Heal
    // At's chain) and, while a cast is pending, a kit or potion. 0 = off.
    public int EmergencyHealAt = 30;
    // Stamina to Health Self: cast at or under this health % ahead of Heal At's chain, when
    // stamina is over StaminaToHealthMinStamina %. 0 = never cast it. Both default to the
    // values that were hard-wired to Emergency Heal At before (30 / 20).
    public int StaminaToHealthAt = 30;
    public int StaminaToHealthMinStamina = 20;
    public bool StopMacroOnDeath = false;
    // Recall On Death (RynthAiPlugin.DeathRecall.cs): after a death while the macro ran, once
    // respawned: 0 off, 1 cast DeathRecallSpellId, 2 use the inventory item named DeathRecallItem
    // (exact name, else the first containing it). Travel Back To My Corpse starts after it.
    public int DeathRecallMode = 0;
    public int DeathRecallSpellId = 48;          // Primary Portal Recall
    public string DeathRecallItem = string.Empty;
    public bool StopMacroOnNoComponents = false;
    // Recall When Low On Components (RynthAiPlugin.LowComponents.cs): tapers at or under LowCompsTaperMin,
    // or a scarab kind (not Diamond) at or under LowCompsScarabMin, stops the macro and recalls home.
    public bool LowCompsRecall = false;
    public int LowCompsTaperMin = 10;
    public int LowCompsScarabMin = 2;
    public bool StopLootingWhenPackFull = true;
    public bool StopMacroWhenPackFull = false;
    public int RestamAt = 30;
    /// <summary>
    /// Once stamina is under Re-stam At (in a fight, or just after one), keep recharging until it reaches this %
    /// (one Revitalize often doesn't get far). 0, or not above Re-stam At = one recharge, as before.
    /// </summary>
    public int RestamTo = 0;
    public int GetManaAt = 40;
    public int TopOffHP = 95;
    public int TopOffStam = 95;
    public int TopOffMana = 95;
    public int HealOthersAt = 50;
    public int RestamOthersAt = 10;
    public int InfuseOthersAt = 10;

    public int MonsterRange = 50;
    // Disengage distance for an already-locked target (hysteresis deadband).
    // 0 = auto (MonsterRange + 3). Must exceed MonsterRange or it's ignored.
    // Without the deadband, the small step-back AC applies on combat-mode entry
    // can push a mob just past MonsterRange, dropping it and flipping to peace,
    // then re-approaching — an endless engage/peace oscillation at the edge.
    public int MonsterDisengageRange = 0;
    // Targeting (2026-10-10): how combat ranks the monsters of the top Priority inside Monster
    // Range (CombatManager.TargetScheme): 0 lowest health (unknown counts as full), 1 closest,
    // 2 farthest, 3 least turning. Ties go to the closest.
    public int TargetSelection;
    // Target Lock: keep the picked target until it dies or goes, leaves Monster Range, or is
    // blacklisted or skipped by a watchdog; a higher-priority monster in range doesn't take it
    // (2026-11-07). Off: a higher priority in range takes over at once, and it switches when
    // another is clearly better by Targeting. On by default; a profile saved before this setting
    // loads it as on.
    public bool TargetLock = true;
    // Fight the monster I select (2026-11-01): a monster the PLAYER selects in game (a click, the
    // radar, another plugin's Selection window) becomes the fight target ahead of the Targeting
    // setting and the Priority tiers, until it dies, can't be reached, or the player selects
    // another monster / deselects. The automatic form of /ra targetlock (CombatManager.SelectedTarget.cs).
    // Off by default; a profile saved before this setting loads it as off.
    public bool FightSelectedTarget;
    internal const string FightSelectedTargetTip =
        "On: in a fight, a monster YOU select (click it, or pick it on the radar or in a Selection window)\n" +
        "is fought first, ahead of Targeting and Priority, until it dies, can't be reached, or you select\n" +
        "another monster or deselect. The bot's own selections never count. Ignored (Priority 0) and\n" +
        "never-attack monsters are not followed (/ra targetlock still forces them). Out to Disengage Range.\n" +
        "/ra fightselected [on|off]";
    public int RingRange = 5;
    // Obsolete since 2026-10-20 (unused here, kept so profiles still load and carry it): the Empyrean
    // Handler lethal-ring evade is Infinite Leaftide's and lives in RynthILT now (its ILT settings,
    // "/ilt settings"), which copied this value once. RynthAi keeps only the generic back-away it
    // asks for (CombatManager.BackAwayFrom, RynthAi.Hub BackAwayFrom).
    public bool HandlerRingEvade = true;
    public int ApproachRange = 4;
    public int MinRingTargets = 4;
    // Blast like a ring: with BlastRange > 0, a rule with Blast on casts a blast only when
    // MinBlastTargets monsters are within BlastRange in the blast's fan (3 projectiles over 90
    // degrees) toward the target; otherwise its other shape. 0 = off: Blast is just a shape.
    public int BlastRange = 0;
    public int MinBlastTargets = 3;
    // Wall spells (2026-10-08): with WallRange > 0, a rule with Wall on casts its element's wall
    // (Slithering Flames, Os' Wall...) when MinWallTargets monsters are within WallRange in the
    // wall's path toward the target, turning to face the target first (a wall flies along the
    // caster's facing). Below a due ring, above a due blast. 0 = off: no walls at all.
    public int WallRange = 0;
    public int MinWallTargets = 3;
    // Nav point reach (VTank "Follow/Nav Min Distance"), yards: how close nav gets to each
    // nav point before moving on to the next, and how close Follow gets to its leader.
    // Metas set it as FollowNavMin (yards) or VTank's NavCloseStopRange (landblock units,
    // x240 = yards; the same VTank setting). Settings ▸ Navigation and the Nav panel show it.
    public float FollowNavMin = 1.5f;
    public const float FollowNavMinLowest = 0.5f, FollowNavMinHighest = 20f;
    public static float ClampFollowNavMin(float yards) =>
        float.IsNaN(yards) ? 1.5f : Math.Clamp(yards, FollowNavMinLowest, FollowNavMinHighest);
    /// <summary>Marker colours, waypoint HUD / labels, breadcrumb trail and recording options.</summary>
    public NavOverlaySettings NavOverlay = new();
    public float NavRingThickness = 6.0f;
    public float NavLineThickness = 6.0f;
    public float NavHeightOffset = 0.05f;
    public float NavSlopeSink = 1.5f;
    public bool  ShowTerrainPassability = true;
    // Nav3D extras, both off by default: labels over the route's waypoints (NavLabelModes:
    // 0 off, 1 numbers, 2 numbers and what happens there) and a line from the character to
    // the waypoint nav is heading for.
    public int   NavLabelMode;
    public bool  NavGuideLine;
    public double MaxMonRange = 12.0;
    public bool SummonPets;
    public int CustomPetRange = 5;
    public int PetMinMonsters = 1;
    public bool PetAutoRefill = true;   // auto-consume Encapsulated Spirits to recharge empty essences
    public bool AdvancedOptions;
    public bool MineOnly = true;
    public bool ShowEditor;
    public double CorpseApproachRangeMax = 10.0;
    public double CorpseApproachRangeMin = 2.0;

    public bool BoostNavPriority;
    /// <summary>
    /// Obsolete since 2026-10-20 (kept so profiles still load and carry it): the Vaulted Dungeons
    /// keeper's master switch is RynthILT's own setting now, copied from here once. RynthAi only
    /// mirrors RynthILT's value into the engine's Settings > Navigation row (ServerSettingGet / Set)
    /// and answers this old value through RynthAi.Hub VaultedDungeonsEnabled for an older RynthILT.
    /// </summary>
    public bool EnableVaultedDungeons;
    public bool BoostLootPriority;
    public bool OpenDoors;
    public float OpenDoorRange = 5.0f;
    /// <summary>Open Doors opens only a closed door on the path of travel (the route's next legs, or straight ahead when not on a route), not every door within OpenDoorRange.</summary>
    public bool OpenDoorsOnPathOnly;
    public bool AutoUnlockDoors;
    public int LootOwnership;
    /// <summary>
    /// Recover your own death corpse ("Corpse of &lt;you&gt;", identified by its "Killed by" line):
    /// when it is within loot range the bot opens it and takes every item on it, whatever the
    /// loot profile, Loot From, value or salvage rules say. On by default (owner, 2026-10-05);
    /// a profile saved before this setting loads it as on.
    /// </summary>
    public bool LootOwnCorpse = true;
    /// <summary>
    /// Out of acid or lava at once (RynthAiPlugin.HazardEscape.cs): the first hotspot hit stops
    /// everything and walks back out, and the spot becomes a hazard the patrol avoids. On by
    /// default (2026-10-07); off for characters that walk through acid on purpose (then the spot
    /// isn't learned either). A profile saved before this setting loads it as on.
    /// </summary>
    public bool EscapeHazards = true;
    /// <summary>
    /// After a death and respawn, travel back to where you died (RynthNav, outdoors only), fight
    /// on the way as usual, recover the corpse, then carry on. Off by default.
    /// </summary>
    public bool TravelToOwnCorpse;
    /// <summary>Not read by the corpse looting (no rare-corpse check exists); kept so saved profiles
    /// round-trip. Hidden in Advanced Settings. The meta/MagTools option of that name sets Mine Only.</summary>
    public bool LootOnlyRareCorpses;
    /// <summary>
    /// Loot Mode (Loot.LootModes): 0 = Profile (the loot profile, the default), 1 = Rares and needed
    /// mana stones only (the loot profile is not used; rares, and mana stones while the mana stone
    /// keep count wants them). Per settings profile; the meta setting LootMode; /ra loot mode.
    /// </summary>
    public int LootMode;
    /// <summary>Also pick up loose ground items that match the loot profile, within the corpse max range.</summary>
    public bool EnableGroundLoot;
    /// <summary>Loot by name: names separated by |. An object in the world with one of these names inside
    /// <see cref="LootByNameRadius"/> is used (a container opens, an item is picked up) while botting.</summary>
    public string LootByNames = string.Empty;
    public float LootByNameRadius = 30f;
    public bool PeaceModeWhenIdle = true;
    /// <summary>After a heal, restam or remana spell with no monster to fight, put the last fight's
    /// weapons (main hand and off hand) back on instead of waiting for the next target.</summary>
    public bool RearmAfterVitals;
    /// <summary>
    /// Weapon swaps (to the wand for a heal or a buff, and Weapons Back After Heals) through peace
    /// mode (2026-10-22, HandSwapSequence): with the server quiet, peace, the moves out of the hands and
    /// the wields go in one frame (the burst: the wand in hand 0.1-0.26 s after the swap begins on
    /// Drakkon); else each step the moment the server answers the last (about 1 s). Before: 4-7 s.
    /// Also combat's own equip when a monster comes with the wand in hand (2026-10-23).
    /// On by default since 2026-10-23, saved under a new name: the opt-in build saved "FastWeaponSwap":
    /// false into every profile it touched, and that no longer loads. Off: the swap as before (*.LegacySwap.cs).
    /// </summary>
    [JsonPropertyName("FastWeaponSwapOn")]
    public bool FastWeaponSwap = true;
    /// <summary>
    /// Rebuff When Idle (VTank's "idlebufftopoff"): between fights, top buffs off early. When the bot
    /// is idle (no fight target, no monster within Monster Range, no corpse to loot, no salvage), a
    /// rebuff starts once a buff has RebuffSecondsRemaining + RebuffIdleMarginMinutes left instead of
    /// waiting for RebuffSecondsRemaining. The regular rebuff is unchanged, in a fight or not.
    /// Read by BuffManager.RebuffLineSec (nothing read it before 2026-10-30). Off by default.
    /// </summary>
    public bool RebuffWhenIdle;
    /// <summary>
    /// Rebuff When Idle's margin, minutes: how much earlier than RebuffSecondsRemaining an idle
    /// top-off may start. Default 2; 0 = the option adds nothing. A profile saved without it loads 2.
    /// </summary>
    public int RebuffIdleMarginMinutes = 2;
    /// <summary>
    /// Re-cast a self buff when its remaining duration drops below this many seconds.
    /// Default 300 (5 minutes). Lower values rebuff more eagerly; very low values
    /// (under ~60s) risk recasting between an old buff dropping and the new one
    /// landing.
    /// </summary>
    public int RebuffSecondsRemaining = 300;
    /// <summary>
    /// When a buff falls under RebuffSecondsRemaining, also refresh every other buff
    /// with less than this many seconds left, so they land together instead of each
    /// interrupting the fight on its own. Buffs with more time are left alone. At or
    /// below RebuffSecondsRemaining only the expiring buff is recast.
    /// </summary>
    public int RebuffTopOffSecondsRemaining = 1200;
    /// <summary>
    /// Cast our own buff even when an item already gives that buff (a permanent, item-granted
    /// enchantment at the same tier or higher). For servers with buff augments, where a player's
    /// own cast is stronger or lasts longer than the item's. On by default: on retail the cost
    /// is a few extra casts, while missing the buff is worse. A profile saved before this
    /// setting existed loads it as on. Off = an item-granted buff at the tier we'd cast counts
    /// as on for good (the behaviour before 2026-10-05).
    /// </summary>
    public bool CastBuffsOverItemBuffs = true;
    /// <summary>
    /// In a buff pass, send the next buff as soon as the server answers the last one with
    /// UseDone(0), without waiting for the client to finish the cast's recoil animation
    /// (Combat/BuffCastChain.cs). Every other gate stays, and a refusal turns it off for the
    /// rest of the pass. On by default; off = every buff waits for the animation (the
    /// behaviour without the chain). A profile saved before this setting existed loads it as on.
    /// </summary>
    public bool FastBuffChain = true;
    /// <summary>
    /// Leave out the weapon enchantments this character has no use for: Blood Drinker / Heart Seeker /
    /// Swift Killer without a melee or missile weapon (listed or in hand), Spirit Drinker / Hermetic
    /// Link without a wand (listed, in hand, or the pack wand buffing would wield), Defender without
    /// either. Also reports, once per run, what was left out. On by default; a profile saved before
    /// this setting existed loads it as on. Off = the weapon enchantments are cast regardless of
    /// listed weapons. A mastery for an untrained skill is never cast, whatever this says.
    /// </summary>
    public bool SkipUnusableBuffs = true;
    /// <summary>
    /// Buff base names the player unticked in Buffing settings (the Protections and Banes grid,
    /// e.g. "Fire Protection Self", "Flame Bane"): left out of the buff list, so never cast and
    /// never waited on. Empty = every buff on, which is also what a profile saved before this
    /// setting existed loads as. Replaced, not edited in place (SetBuffEnabled), so a reader
    /// holding the old list never sees it change under it.
    /// </summary>
    public List<string> DisabledBuffs
    {
        get => _disabledBuffs;
        set => _disabledBuffs = value ?? new List<string>();
    }
    private List<string> _disabledBuffs = new();

    public bool IsBuffDisabled(string buffBaseName)
    {
        foreach (string b in _disabledBuffs)
            if (string.Equals(b, buffBaseName, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    public void SetBuffEnabled(string buffBaseName, bool enabled)
    {
        if (enabled == !IsBuffDisabled(buffBaseName)) return;
        var next = new List<string>(_disabledBuffs);
        if (enabled) next.RemoveAll(b => string.Equals(b, buffBaseName, StringComparison.OrdinalIgnoreCase));
        else next.Add(buffBaseName);
        _disabledBuffs = next;
    }
    /// <summary>
    /// Cast the armor spells (Impenetrability and the Banes still ticked above) on the shield
    /// listed in the Items list too, after the armor, on the armor's timers (BuffManager.Shield.cs).
    /// The self-cast never reaches it: ACE won't wield the wand beside a shield, so buffing puts
    /// the shield into a pack first. On by default; a profile saved before this setting existed
    /// loads it as on. No shield listed: nothing changes.
    /// </summary>
    public bool BuffShield = true;
    public bool StartMacroOnLogin;
    public bool PatrolOnLogin;
    /// <summary>
    /// Resume Meta After Reload (2026-11-07, Tom: "remembers which meta state you're in and restarts in
    /// that state so Drakkon doesn't recall back every time"). A hot reload carries the meta's whole
    /// runtime over (Meta/MetaResume.cs): state, variables, fired rules, call stack, watchdog, route.
    /// A real login still starts in Default.
    /// </summary>
    public bool ResumeMetaOnReload = true;
    /// <summary>
    /// Decal bridge only: when VTank's macro starts, RynthAi's macro stops, and it won't start
    /// while VTank runs (one bot per client). On by default; a profile saved before this
    /// setting existed loads it as on. Without Decal it does nothing.
    /// </summary>
    public bool YieldToVTank = true;

    /// <summary>
    /// Legacy (unused by RynthAi since 2026-10-13): the old Settings > Misc "Server Features" list
    /// that turned on the ILT Hub. The ILT features moved to the RynthILT plugin, which keeps its
    /// own list and reads these two fields once, on a character's first login with it
    /// (IltMigration). Kept so a profile still carries them until then; no UI, no bridge.
    /// </summary>
    public List<string>? FeatureServerNames { get; set; }
    /// <summary>Legacy (see <see cref="FeatureServerNames"/>): the old "Force enable on this server" override.</summary>
    public bool ForceServerFeatures;

    public int BlacklistAttempts = 3;
    public int BlacklistTimeoutSec = 30;
    /// <summary>
    /// Grace after an offensive cast before it's judged a "miss". A cast only
    /// counts toward BlacklistAttempts if no damage landed on the target within
    /// this window — so blacklisting is driven by confirmed no-damage casts,
    /// not wall-clock time / damage-packet latency. Default 1500ms.
    /// </summary>
    public int BlacklistCastSettleMs = 1500;
    /// <summary>
    /// Blacklist a target after being engaged with it for this many seconds without
    /// dealing any damage. 0 = disabled. Default 60s.
    /// </summary>
    public int TargetNoProgressTimeoutSec = 0; // 0 = disabled; set to e.g. 120 to blacklist stuck targets

    /// <summary>
    /// User-configured "never attack" list. Any monster whose name CONTAINS one
    /// of these entries (case-insensitive) is excluded from combat target
    /// acquisition entirely — never scanned, faced, or attacked. This is a manual
    /// do-not-attack list, separate from the automatic no-damage blacklist above.
    /// Edited in Advanced → Combat → Monster Blacklist.
    /// </summary>
    public List<string> MonsterNameBlacklist { get; set; } = new();

    public int GiveQueueIntervalMs = 0;

    /// <summary>
    /// Obsolete since 2026-10-20 (kept so profiles still load and carry it): the item augmentation
    /// count override for the T11 "Can Wield" loot key is RynthILT's now ("/ilt t11 augs", copied from
    /// here once), and RynthILT reports the counters it reads from "/aug" (RynthAi.Hub SetPlayerCounter).
    /// </summary>
    public int T11ItemAugsOverride = -1;

    public int SpellCastIntervalMs = 400;

    /// <summary>
    /// Stuck-cast breaker (Plugins/Shared/ActionPacer.cs): the longest a cast may stay unanswered
    /// (ms from the send; no result, no UseDone) before it is broken with a jump (a Peace mode
    /// request when the engine can't jump). It fires sooner, about 2 s past the spell's normal
    /// cycle, when that is shorter. Nothing is cast meanwhile once the server says 'too busy'.
    /// 0 = no breaker.
    /// </summary>
    public int StuckCastBreakMs = 8000;

    /// <summary>
    /// Recovery vitals (Heal Self, Stamina to Health, Revitalize, Stamina to Mana) refused for
    /// "not enough mana" are tried again after this many ms, for a better Mana Conversion roll
    /// (ACE rolls the cost on every attempt and a refusal costs nothing). Clamped to 200..5000;
    /// at most 5 tries a second and 20 refusals in a row, then the old 15 s family rest.
    /// </summary>
    public int RecoveryRetryMs = 200;

    /// <summary>
    /// HP failsafe: at or under Emergency Heal At with heals failing for a while, the stuck-cast
    /// breaker runs and the Lifestone Recall spell is cast at once (BuffManager.HpFailsafe.cs).
    /// Off: the breaker still runs, no recall.
    /// Saved under a new name since 2026-10-22, so it is off for everyone until ticked again: the
    /// recall stops the macro (heals included) in the middle of a fight, and it was killing players.
    /// The 10-18 default change only reached profiles that never saved it; an old saved
    /// "HpFailsafeRecall": true (the default until then) no longer loads.
    /// </summary>
    [JsonPropertyName("HpFailsafeRecallOptIn")]
    public bool HpFailsafeRecall = false;

    /// <summary>
    /// Minimum delay between offensive (war/void) COMBAT spell casts, in ms.
    /// Combat magic uses this instead of <see cref="SpellCastIntervalMs"/> so
    /// attack pacing can be slowed (~1-2s) without slowing buff chains. Default
    /// 1500ms ("a second or two") — spacing offensive casts stops back-to-back
    /// "You're too busy!" refusals that drop casts and cost kills. ≤0 ⇒ 1500.
    /// </summary>
    public int AttackSpellIntervalMs = 1500;

    public int MeleeAttackPower = -1;
    public int MissileAttackPower = -1;
    /// <summary>
    /// Physical-attack path. FALSE (default since 2026-09-04) = the direct
    /// explicit-target path: Event_TargetedMelee/MissileAttack take the target
    /// id as an argument and never touch AC's single selection global
    /// (0x00871E54), so combat no longer clobbers the inventory item you have
    /// selected. TRUE = AC's native ClientCombatSystem pipeline, which has no
    /// target argument and therefore must be preceded by SelectItem(targetId) —
    /// that SelectItem was the last selection clobber in the combat hot path.
    ///
    /// This defaulted to true until now for one reason: the direct path does not
    /// turn the character to face the monster, and swings/arrows at the wrong
    /// heading go nowhere (ExplicitTarget_Combat_Findings_2026-06-03.md §5). The
    /// facing servo in CombatManager.OnHeartbeat now covers both melee and
    /// missile on the non-native path, which is exactly what that blocker asked
    /// for, so the default flips.
    ///
    /// Kept as a setting rather than hard-gated: the servo has not been soaked
    /// against a moving target across a long session, and re-ticking this box in
    /// the Advanced settings UI restores the old behaviour live, without a
    /// rebuild, if the swings look wrong.
    /// </summary>
    public bool UseNativeAttack = false;
    public bool UseRecklessness;
    /// <summary>
    /// The range a monster rule's "Variable" attack power picks from, in percent of the bar
    /// (a new value every swing or shot). "Reckless + Variable" also stays inside the
    /// Recklessness band (20-80%). See AttackPowerPlanner.
    /// </summary>
    public int VariablePowerMin = 50;
    public int VariablePowerMax = 100;

    // ── Environment hits (2026-10-12, Combat/EnvironmentImpact.cs) ─────────────
    /// <summary>
    /// What missile combat does once a target has taken EnvironmentImpactLimit "Your missile
    /// attack hit the environment." within EnvironmentImpactWindowSec: an EnvironmentResponse
    /// (0 Off, 1 Move closer, 2 New target, 3 Move closer then new target). Off by default.
    /// </summary>
    public int MissileEnvironmentResponse;
    /// <summary>The same for war/void casts whose projectiles stop short of the target. Off by default.</summary>
    public int MagicEnvironmentResponse;
    /// <summary>Environment hits on one target, within the window, that trip the response.</summary>
    public int EnvironmentImpactLimit = 2;
    /// <summary>The window (seconds) the hits are counted in.</summary>
    public int EnvironmentImpactWindowSec = 20;
    /// <summary>How far (metres) each "move closer" goes toward the target.</summary>
    public int EnvironmentApproachMeters = 6;
    /// <summary>Never approaches nearer than this (metres); already this close, a new target is picked.</summary>
    public int EnvironmentMinDistance = 3;
    /// <summary>How long (seconds) a target left for its environment hits is skipped.</summary>
    public int EnvironmentSkipSec = 30;
    /// <summary>
    /// Pull-away kite (2026-10-16, Combat/KiteModes.cs): how far (metres) to drag a PullAway
    /// monster from its lock-spot origin before stopping to kill it. Per-mob on/off is
    /// MonsterRule.Kite (Default follows the DEFAULT row).
    /// </summary>
    public int KitePullMeters = 20;
    /// <summary>Land a hit before fleeing so aggro sticks (Off = run immediately).</summary>
    public bool KiteTagFirst = true;
    /// <summary>
    /// Combat and buffing only ever wield weapons from the Items list. With this on (the
    /// default), a character whose Items list has no wand at all may still buff and cast with a
    /// caster from its pack (by object class, never by name). Any listed wand turns it off.
    /// </summary>
    public bool WieldUnlistedWandWhenNoneListed = true;
    /// <summary>
    /// The off hand (the Shield slot) when the monster rule doesn't choose one: "Auto" (a listed
    /// shield with a one-handed melee weapon), "Shield", "Weapon" (dual wield a second listed
    /// one-handed weapon; needs the Dual Wield skill) or "None" (never touched). A rule chooses
    /// with MonsterRule.OffhandId (OffhandRules.RuleAuto..RuleNone). See OffhandPlanner.
    /// </summary>
    public string OffhandDefault = "Auto";
    /// <summary>Auto: dual wield rather than a shield when the Dual Wield skill is trained and a
    /// second one-handed melee weapon is listed. Off by default (the shield).</summary>
    public bool PreferDualWield;
    /// <summary>
    /// Weapon swaps (CombatManager.SwapDeferReason). A switch from one live target to another
    /// waits this long (ms) before a swap starts, so a target that changes again doesn't cost a
    /// wasted swap. A lock after a kill or drop swaps at once. 0 = never wait.
    /// </summary>
    public int WeaponSwapSettleMs = 750;
    /// <summary>No new swap (same kind) onto a target at or under this health %: finish it with
    /// the weapon in hand. 0 = off.</summary>
    public int WeaponSwapMinHealthPct = 25;
    /// <summary>A weakness pick (Auto) swaps only for at least this % more damage than the weapon
    /// in hand (WeaponPlanner.KeepInHand). Damage tab, slayer and Monsters rule picks always swap. 0 = off.</summary>
    public int WeaponSwapMinGainPct = 20;
    public int MeleeAttackHeight = 1;
    public int MissileAttackHeight = 1;

    // Projectile arc velocities for missile-weapon LoS checks.
    // Lower velocity = higher arc. Must approximate the AC client trajectory
    // so dungeon-ceiling hits are detected.
    public bool  UseArcs              = true;
    public float BowArcVelocity       = 25.0f;
    public float CrossbowArcVelocity  = 40.0f;
    public float AtlatlArcVelocity    = 22.0f;
    /// <summary>
    /// Arc spells' HORIZONTAL speed (m/s). ACE flies every player arc at a fixed 40 m/s across
    /// the ground under gravity 9.8 (MissileBallistics.SolveLateral). Combat casts an Arc only
    /// when that path reaches the target, else the rule's other shape (or Bolt). Until
    /// 2026-10-04 this was a launch speed for a flat-ground model, default 25:
    /// <see cref="MigrateMagicArcVelocity"/> moves that old default to 40.
    /// </summary>
    public float MagicArcVelocity     = Raycasting.MissileBallistics.AceArcSpellSpeed;

    /// <summary>A saved MagicArcVelocity under today's model: the old default (25, a launch
    /// speed for the flat-ground model, which as a horizontal speed would make arcs 2.6x higher
    /// than ACE flies them) and nonsense values become 40.</summary>
    public static float MigrateMagicArcVelocity(float saved)
        => float.IsFinite(saved) && saved > 0f && saved != 25.0f ? saved : Raycasting.MissileBallistics.AceArcSpellSpeed;
    /// <summary>
    /// Extra headroom (meters) a missile's arc must have at mid-flight for LoS to pass,
    /// on top of the modelled flight path (launch speed above, AC gravity 9.8). Covers
    /// what the model can't know exactly: launch height, the real launch speed, the
    /// projectile's size. Raise it if shots still hit ceilings, lower it if targets
    /// under a ceiling the arrows clear are skipped. Only used with UseArcs.
    /// </summary>
    public float MissileArcClearance  = 0.5f;
    /// <summary>
    /// Log each in-range target's LoS verdict (straight line / arc, how high the arc
    /// rises, where it hits) and the missile weapon's known launch speed. For tuning
    /// the arc settings; off by default (a few lines a second in a crowd).
    /// </summary>
    public bool  LosDebugLog;

    public bool EnableFPSLimit = true;
    public int TargetFPSFocused = 60;
    public int TargetFPSBackground = 30;

    // Minimum buffed skill level required to cast spells of each tier — for COMBAT casts.
    // Defaults are tuned above AC's hard minimums (1/50/100/150/200/250/300/350) to avoid fizzles.
    public int MinSkillLevelTier1 = 35;
    public int MinSkillLevelTier2 = 85;
    public int MinSkillLevelTier3 = 135;
    public int MinSkillLevelTier4 = 185;
    public int MinSkillLevelTier5 = 235;
    public int MinSkillLevelTier6 = 285;
    public int MinSkillLevelTier7 = 335;
    public int MinSkillLevelTier8 = 435;

    // Minimum buffed skill level required to cast self-buffs of each tier.
    // Defaults match the spell combat thresholds so new installs work consistently.
    public int BuffMinSkillLevelTier1 = 35;
    public int BuffMinSkillLevelTier2 = 85;
    public int BuffMinSkillLevelTier3 = 135;
    public int BuffMinSkillLevelTier4 = 185;
    public int BuffMinSkillLevelTier5 = 235;
    public int BuffMinSkillLevelTier6 = 285;
    public int BuffMinSkillLevelTier7 = 335;
    public int BuffMinSkillLevelTier8 = 435;

    // On for a new character (2026-10-20): with it off only stones already charged refill worn gear, and
    // nothing charges them, so suits ran dry for players who never found the switch. A saved profile keeps its choice.
    public bool EnableManaTapping   = true;
    public int  ManaTapMinMana      = 2500;
    public int  ManaStoneKeepCount  = 5;

    // ── AutoVendor (UtilityBelt AutoVendor; /ub autovendor, /ub vendor) ────────
    /// <summary>Run AutoVendor when a vendor opens (and allow /ub autovendor). UB defaults this on;
    /// RynthAi defaults it off because the plugin auto-updates and selling can't be undone.</summary>
    public bool AutoVendorEnabled = false;
    public bool AutoVendorEnableBuying = true;
    public bool AutoVendorEnableSelling = true;
    /// <summary>Only print what would be bought and sold. On by default in RynthAi (UB: off).</summary>
    public bool AutoVendorTestMode = true;
    /// <summary>Send "AutoVendor finished: ..." (and fatal/failed lines) as a /tell to yourself, for metas.</summary>
    public bool AutoVendorThink = false;
    public bool AutoVendorShowMerchantInfo = true;
    public bool AutoVendorOnlyFromMainPack = false;
    /// <summary>Attempts to open a vendor on /ub vendor open[p].</summary>
    public int AutoVendorTries = 4;
    /// <summary>Milliseconds between /ub vendor open attempts.</summary>
    public int AutoVendorTriesTime = 5000;

    // ── AutoTrade (UtilityBelt AutoTrade; /ub autotrade, /ra autotrade) ────────
    /// <summary>Fill the trade window from a loot profile when a trade opens (UB default: off).</summary>
    public bool AutoTradeEnabled = false;
    /// <summary>Only print what would be added to the trade window.</summary>
    public bool AutoTradeTestMode = false;
    /// <summary>Send "AutoTrade finished: ..." and "Trade accepted: ..." as a /tell to yourself, for metas.</summary>
    public bool AutoTradeThink = false;
    public bool AutoTradeOnlyFromMainPack = false;
    /// <summary>Accept the trade once every item AutoTrade added is in the window.</summary>
    public bool AutoTradeAutoAccept = false;
    /// <summary>Name patterns (.NET regex) whose accepted trades this character accepts too.
    /// The autoAcceptList.json files (/ub autotrade autoaccept ...) are read as well.</summary>
    public List<string> AutoTradeAutoAcceptChars { get; set; } = new();

    public List<MonsterRule> MonsterRules { get; set; } = new();
    public List<ItemRule> ItemRules { get; set; } = new();
    /// <summary>Loose ammo the monster rules can prefer. Edited by the local monsters window.</summary>
    public List<AmmoRule> AmmoRules { get; set; } = new();
    public List<ConsumableRule> ConsumableRules { get; set; } = new();
    public List<BuffRule> BuffRules { get; set; } = new();
    public List<MetaRule> MetaRules { get; set; } = new();

    /// <summary>
    /// Guards every read/enumerate/mutate of <see cref="MetaRules"/>. The list
    /// is touched from the plugin-tick thread (MetaManager.Think) AND the
    /// Avalonia dispatcher thread (MetaPanel poll → BuildMetaJson /
    /// HandleMetaCommand). List&lt;T&gt; is not thread-safe; concurrent
    /// enumerate+mutate corrupts the heap. Hold this lock for the full duration
    /// of any MetaRules access.
    /// </summary>
    [JsonIgnore]
    public object MetaRulesLock { get; } = new();

    /// <summary>
    /// Bumped on an *in-place* rule edit (a slot replaced, possibly changing a
    /// rule's State) so MetaManager's per-state index rebuilds. Add/delete/load
    /// change the list ref or count and are detected without this.
    /// </summary>
    [JsonIgnore]
    public int MetaRulesStructuralVersion;

    private Dictionary<string, List<string>> _embeddedNavs =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// NAV routes embedded inside the currently-loaded .af/.met macro,
    /// keyed by name. Persisted so meta EmbedNav actions still resolve
    /// after a client restart without reloading the source file.
    /// Setter re-wraps incoming dict with OrdinalIgnoreCase comparer so
    /// JSON round-trip keeps case-insensitive lookups working.
    /// </summary>
    public Dictionary<string, List<string>> EmbeddedNavs
    {
        get => _embeddedNavs;
        set => _embeddedNavs = value != null
            ? new Dictionary<string, List<string>>(value, StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
    }
    /// <summary>
    /// Set once this profile's weapons and consumables have been checked against
    /// the character's own pack (new profiles are filled from it; older ones had
    /// entries copied from another character removed). See TickPackSetup.
    /// </summary>
    public bool ProfileItemsChecked;

    public string SelectedProfile = "Default";
    [JsonIgnore] public NavRouteParser CurrentRoute { get; set; } = new();
    public int ActiveNavIndex;

    /// <summary>
    /// True for a Once route that has walked off its last point (the nav engine leaves the
    /// index past the end). It stays finished over a macro stop/start and a meta turning nav
    /// on, until it is loaded again, edited, or restarted (/ra nav restart, or
    /// <see cref="RearmFinishedNavRoute"/> from the player's own nav-on controls).
    /// </summary>
    public bool NavRouteFinished()
    {
        var route = CurrentRoute;
        return route != null && route.RouteType == NavRouteType.Once
            && route.Points.Count > 0 && ActiveNavIndex >= route.Points.Count;
    }

    /// <summary>
    /// The player turned nav on (the Nav switch, a Start Navigation button): a finished Once
    /// route runs again from [0], as re-enabling nav did before 2026-10-28. Metas and scripts
    /// that switch nav on don't call this, so a stop/start replaying a meta's
    /// "/vt opt set enablenav true" no longer re-runs a finished route. True if it rewound.
    /// </summary>
    public bool RearmFinishedNavRoute()
    {
        if (!NavRouteFinished()) return false;
        ActiveNavIndex = 0;
        return true;
    }

    public bool ShowAdvancedWindow;
    public int SelectedAdvancedTab;

    // ── Persisted window/UI state ───────────────────────────────────────────
    public float WindowPosX = -1f;
    public float WindowPosY = -1f;
    public float WindowSizeX = 430f;
    public float WindowSizeY = 452f;
    public bool WindowLocked;
    public bool DashboardVisible = true;
    public bool DashboardMinimized;
    public float BgOpacity = 0.95f;
    public bool DashShowWeapons;
    public bool DashShowLua;
    public bool DashShowNavigation;
    public bool DashShowMacroRules;
    public bool DashShowMonsters;
    public bool DashShowDungeonMap;
    public bool  MapShowDoors         = true;
    public bool  MapShowCreatures     = true;
    public bool  MapShowToolbar       = true;
    public float MapBgOpacity         = 1.0f;
    public bool  MapRotateWithPlayer  = false;

    // Radar dungeon-wall overlay
    public bool  ShowRadarWalls        = false; // WIP — renders as black box, disabled by default
    public float RadarWallWorldRange   = 35f; // world units shown edge-to-edge on the radar

    // When true, the engine suppresses the vanilla retail radar entirely.
    public bool  SuppressRetailRadar   = false;

    // When true, the engine no-ops the gmPowerbarUI notices so the vanilla
    // attack/magic power bar never appears on screen.
    public bool  SuppressRetailPowerbar = false;

    // Custom chat viewer
    public bool  ShowRynthChat         = false;
    public float ChatOpacity           = 0.15f;
    public int   ChatMaxLines          = 500;
    public bool  ChatShowTimestamps    = false;
    public bool  ChatClickThrough      = false;   // mouse passes through to game; gear stays clickable

    // When true, the custom RynthRadar widget is rendered.
    public bool  ShowRynthRadar        = false;
    public bool  RadarRotateWithPlayer = false;   // false = north-up; true = player always faces up
    public float RadarOpacity          = 0.85f;
    public float RadarZoom             = 3.5f;    // pixels per world unit
    public bool  RadarShowMonsters     = true;
    public bool  RadarShowNpcs         = true;
    public bool  RadarShowPortals      = true;
    public bool  RadarShowDoors        = true;
    // Grid-cell radius (0.5m per cell) of the "visited" paint stamp placed each
    // frame around the player. Higher = walls light up from further away.
    public int   RadarWallPaintRadius  = 3;
    public bool  RadarCircular         = false;   // false = square frame, true = circular
    public bool  RadarClickThrough     = false;   // mouse passes through to the game
    // Persisted radar window placement. -1 = never saved; the first session
    // applies the default size and captures the position once it's shown.
    public float RadarPosX             = -1f;
    public float RadarPosY             = -1f;
    public float RadarSizeX            = 260f;
    public float RadarSizeY            = 284f;

    [JsonIgnore]
    public readonly string[] AdvancedTabs =
    {
        "Display", "UI", "Misc", "Recharge", "Melee Combat", "Spell Combat",
        "Ranges", "Navigation", "Buffing", "Crafting", "Looting", "Vendoring"
    };

    [JsonIgnore]
    public bool ForceStateReset { get; set; }

    /// <summary>Set when a meta file is loaded: the next meta tick empties the
    /// CallState/Return stack.</summary>
    [JsonIgnore]
    public bool MetaCallStackReset { get; set; }

    [JsonIgnore]
    public string NavStatusLine = string.Empty;

    [JsonIgnore]
    public bool NavIsStuck = false;

    /// <summary>Nav is held in a dungeon by a route none of whose waypoints is in it (a landscape
    /// route, another dungeon's): the Dungeon Patrol is the way to run here, and the nav window
    /// offers its button (NavigationEngine.AreaMismatchHold sets it, the hold ending clears it).</summary>
    [JsonIgnore]
    public bool NavAreaHoldOffersPatrol = false;

    // Runtime-only: the object id of the portal/NPC the nav engine is actively
    // using (resolved by FirePortalNpcUse), or 0 when not using one. Lets the
    // marker renderer draw a ring + line to the portal's REAL position, since
    // the PortalNPC waypoint's stored coordinate is an unreliable placeholder.
    [JsonIgnore]
    public uint ActivePortalObjId = 0;

    // Fellowship-follow: when on, nav steers toward the live target instead of a
    // route. Runtime-only (off each session; enable via /ra follow on).
    [JsonIgnore]
    public bool FollowMode = false;

    // Resolved object id of the follow target (the fellowship leader), published
    // by the plugin each tick; 0 when not in a fellowship / target not loaded.
    [JsonIgnore]
    public uint FollowTargetId = 0;
    /// <summary>A character picked by name (/ra follow Name, /ub follow[p]); wins over the fellowship leader. Session only.</summary>
    [JsonIgnore] public uint FollowNamedTargetId;

    public LegacyUiSettings()
    {
    }

    public void EnsureDefaultRule()
    {
        List<MonsterRule> existingDefaults = MonsterRules.Where(m => m.Name.Equals("Default", StringComparison.OrdinalIgnoreCase)).ToList();
        MonsterRule trueDefault;

        if (existingDefaults.Count > 0)
        {
            trueDefault = existingDefaults[0];
            MonsterRules.RemoveAll(m => m.Name.Equals("Default", StringComparison.OrdinalIgnoreCase));
        }
        else
        {
            trueDefault = new MonsterRule { Name = "Default", Priority = 1, DamageType = "Auto", WeaponId = 0 };
        }

        MonsterRules.Insert(0, trueDefault);
    }
}

public sealed class MonsterRule
{
    public string Name { get; set; } = "New Monster";
    public string Category { get; set; } = "";
    public string MatchExpression { get; set; } = "";
    public int Priority { get; set; } = 1;
    public string DamageType { get; set; } = "Auto";
    /// <summary>The kind of damage to fight with when the character uses more than one: Auto, Melee,
    /// Missile, "War Magic" or "Void Magic" (see DamageStyles). On a monster's own rule, Auto follows
    /// the Default row's; on the Default row, Auto is the kind of the first usable weapon in the
    /// Items list. Off in files from before 2026-10-16.</summary>
    public string PreferredDamage { get; set; } = "Auto";
    public int WeaponId { get; set; }
    public bool Fester { get; set; }
    public bool Broadside { get; set; }
    public bool GravityWell { get; set; }
    public bool Imperil { get; set; }
    public bool Yield { get; set; }
    public bool Vuln { get; set; }
    /// <summary>The Void debuffs (VTank's Cs, Cp, DC, WC and FC columns), cast with Void Magic:
    /// the Nether damage-over-time spells Corrosion, Corruption and Destructive Curse, then
    /// Weakening Curse (less damage from the monster) and Festering Curse (less healing on it).
    /// Off in files from before 2026-10-27.</summary>
    public bool Corrosion { get; set; }
    public bool Corruption { get; set; }
    public bool DestructiveCurse { get; set; }
    public bool WeakeningCurse { get; set; }
    public bool FesteringCurse { get; set; }
    public bool UseArc { get; set; }
    public bool UseRing { get; set; }
    public bool UseStreak { get; set; }
    public bool UseBolt { get; set; } = true;
    /// <summary>Blast spells (Flame Blast, Frost Blast...: a spread of projectiles). Off in files
    /// from before 2026-10-03. Wins over Bolt when both are on; see CombatManager.PickBaseShape.</summary>
    public bool UseBlast { get; set; }
    /// <summary>Wall spells (Slithering Flames, Os' Wall...: a row of projectiles flying straight
    /// ahead). An override like the ring, cast only with Wall Range on and enough monsters in the
    /// wall's path; a wall-only rule casts Bolt between walls. Off in files from before 2026-10-08.</summary>
    public bool UseWall { get; set; }
    public string ExVuln { get; set; } = "None";
    /// <summary>The off hand for this rule: 0 = the global setting (OffhandDefault), 1 Auto,
    /// 2 Shield, 3 Offhand weapon, 4 None (OffhandRules.Rule*); any other value is a listed
    /// item to wield there (the old per-rule picker).</summary>
    public int OffhandId { get; set; }
    /// <summary>Optional loose ammo stack id for missile combat while this rule matches (0 = auto).</summary>
    public int PreferredAmmoItemId { get; set; }
    public string PetDamage { get; set; } = "PAuto";
    /// <summary>Debuffs the player typed in (Damage panel), comma separated: a spell's base
    /// name ("Corrosion Vulnerability Other") casts its best known tier; a full name casts as is.</summary>
    public string CustomDebuffs { get; set; } = "";
    /// <summary>Melee/missile attack power against the monsters this rule covers: one of
    /// AttackPowerModes.All. "Default" = the DEFAULT line's choice (on the DEFAULT line: the global
    /// Melee/Missile Power settings). Rules saved before 2026-10-12 load as Default.</summary>
    public string AttackPower { get; set; } = AttackPowerModes.Default;
    /// <summary>Combat pull-away for this rule: one of KiteModes.All. "Default" = the DEFAULT
    /// line's choice (on the DEFAULT line: Off). Rules saved before 2026-10-16 load as Default.</summary>
    public string Kite { get; set; } = "Default";
}

public sealed class BuffRule
{
    public string SpellName { get; set; } = string.Empty;
    public int SpellId { get; set; }
    public bool Enabled { get; set; } = true;
}

public sealed class ItemRule
{
    /// <summary>Action string for a listed weapon (VirindiTank import and the ILT gear importer).</summary>
    public const string WeaponAction = "Weapon";
    /// <summary>Action string for a listed shield.</summary>
    public const string ShieldAction = "Shield";

    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Action { get; set; } = "Loot";
    public string Element { get; set; } = "Slash";
    /// <summary>Where <see cref="Element"/> came from: "properties" (DamageType after an identify),
    /// "icon" (UiEffects), "rending" (an imbue), "name", "set" (picked in the Items panel), or ""
    /// (not known, or an entry from before sources were kept).</summary>
    public string ElementSource { get; set; } = "";
    /// <summary>Display only (filled by WeaponList.ForDisplay for the Items panel, never read back):
    /// Melee, Missile, "War Magic", "Void Magic", Shield, Ammo, or "" when the item isn't known.</summary>
    public string Kind { get; set; } = "";
    /// <summary>
    /// False = "No buff", read for a listed SHIELD only: none of the armor spells are aimed at it
    /// (BuffManager.Shield.cs), and its timers never make buffing due. On a weapon or wand it does
    /// nothing. True for a new entry and for every profile saved before it was read (the field was
    /// only ever written true). Set from the engine's Items face through <see cref="NoBuff"/>.
    /// </summary>
    public bool KeepBuffed { get; set; } = true;

    /// <summary>
    /// Items bridge wire field only (ItemsBridgePayload.Weapons), never saved in a profile: true =
    /// "No buff" (<see cref="KeepBuffed"/> off). WeaponList.ForDisplay fills it for every entry;
    /// WeaponList.MergeEdited applies it back for a shield entry only, and an absent field (an
    /// engine that predates it) leaves KeepBuffed as saved.
    /// </summary>
    [JsonPropertyName("noBuff")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? NoBuff { get; set; }

    /// <summary>True when buffing leaves this item alone (<see cref="KeepBuffed"/> off; shields only).</summary>
    public bool ExcludedFromBuffing() => !KeepBuffed;

    /// <summary>True when this row is a shield (off hand), not a main-hand weapon.</summary>
    public bool IsShield() => string.Equals(Action, ShieldAction, StringComparison.OrdinalIgnoreCase);
}

/// <summary>Loose ammo the monster rules can prefer (bow, crossbow, atlatl, or Auto).</summary>
public sealed class AmmoRule
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    /// <summary>Bow, Crossbow, Atlatl, or Auto (match any launcher).</summary>
    public string Category { get; set; } = "Auto";
}

/// <summary>JSON wire-format types used by the engine-side Avalonia MonstersPanel.</summary>
public sealed class MonstersBridgePayload
{
    public List<MonsterRule> Rules { get; set; } = new();
    public List<MonsterBridgeItem> Items { get; set; } = new();
    public string CurrentTargetName { get; set; } = string.Empty;
    /// <summary>Captured creature data per rule name (uppercased), filled by the plugin.</summary>
    public Dictionary<string, MonsterCapturedInfo> Captured { get; set; } = new();
}

public sealed class MonsterBridgeItem
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class MonsterCapturedInfo
{
    public uint MaxHealth { get; set; }
    public int  ArmorLevel { get; set; }
    public string WeakestType { get; set; } = string.Empty;
    public double WeakestValue { get; set; } = 1.0;
    public int Samples { get; set; }
}

public sealed class ConsumableRule
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Type { get; set; } = "General";
}

/// <summary>Bridge payload for the engine-side Avalonia ItemsPanel.</summary>
public sealed class ItemsBridgePayload
{
    /// <summary>Copies for the face (WeaponList.ForDisplay). Each carries "noBuff" (ItemRule.NoBuff,
    /// true = not buffed); on write-back it is applied for shield entries only.</summary>
    public List<ItemRule>       Weapons           { get; set; } = new();
    public List<ConsumableRule> Consumables       { get; set; } = new();
    public bool                 EnableManaTapping { get; set; } = true;   // absent from an old profile = the new default
    public int                  ManaTapMinMana    { get; set; }
    public int                  ManaStoneKeepCount { get; set; }
    public string               CurrentTargetName { get; set; } = string.Empty;
    /// <summary>Listed weapons the character no longer carries (not in the pack, not wielded), by
    /// id: the engine's Items face shows them as missing. Display only, never read back.</summary>
    public List<int>            MissingIds        { get; set; } = new();
}

// ── Nav bridge types ─────────────────────────────────────────────────────────

public sealed class NavBridgePoint
{
    public int    Idx    { get; set; }
    public string Type   { get; set; } = string.Empty;  // "Point", "Recall", "Pause", "Chat", "PortalNPC"
    public string Desc   { get; set; } = string.Empty;  // NavPoint.ToString()
    public string Text   { get; set; } = string.Empty;  // Chat points: the command, for editing
    public double NS     { get; set; }
    public double EW     { get; set; }
    public double Z      { get; set; }
}

public sealed class NavBridgePayload
{
    public string            ActiveNavName    { get; set; } = string.Empty;
    public string            NavStatusLine    { get; set; } = string.Empty;
    public bool              NavIsStuck       { get; set; }
    public bool              MacroRunning     { get; set; }
    public bool              NavigationEnabled{ get; set; }
    public int               RouteType        { get; set; }  // 1=Circular,2=Linear,3=Follow,4=Once
    public int               ActiveNavIndex   { get; set; }
    public List<string>      NavFiles         { get; set; } = new();
    public List<NavBridgePoint> Points        { get; set; } = new();
    /// <summary>Map editing commands understood (NavMapEdit.Version): addPointAt, movePointTo,
    /// setActive. The engine's Dungeon Map only offers editing when this is 1 or more.</summary>
    public int               MapEdit          { get; set; }
    /// <summary>NavOverlaySettings.TrackBreadcrumbs (Nav panel "Breadcrumbs" toggle).</summary>
    public bool              TrackBreadcrumbs { get; set; }
    /// <summary>NavOverlaySettings.ShowRouteMarkers (Nav panel "Route overlay" toggle).</summary>
    public bool              ShowRouteOverlay { get; set; }
    /// <summary>Route recording is on (/ra navrec).</summary>
    public bool              IsRecording      { get; set; }
    /// <summary>Breadcrumb trail since the last teleport: point count and walked yards.</summary>
    public int               TrailPoints      { get; set; }
    public double            TrailYards       { get; set; }
    /// <summary>Result of the last route edit ("Route reversed."), empty once it is a few seconds old.</summary>
    public string            EditStatus       { get; set; } = string.Empty;
}

/// <summary>One-shot command sent from the Avalonia NavPanel to the plugin.</summary>
public sealed class NavCommand
{
    public string Cmd       { get; set; } = string.Empty;
    public int    SpellId   { get; set; }
    public int    Index     { get; set; }
    public int    RouteType { get; set; }
    public int    AddMode   { get; set; }   // 0=End, 1=Above, 2=Below
    public int    InsertAt  { get; set; } = -1;
    public string NavName   { get; set; } = string.Empty;
    public string Text      { get; set; } = string.Empty;   // addChat: the command or text
    public double NS        { get; set; }                   // addPointAt / movePointTo (nav coords)
    public double EW        { get; set; }
    public double Z         { get; set; }                   // nav Z (world Z / 240)
    public bool   On        { get; set; }                   // setBreadcrumbs / setRouteOverlay / setRecording
    public List<int> Indices { get; set; } = new();         // movePoints / duplicatePoints / deletePoints
    public int    Delta     { get; set; }                   // movePoints: -1 up, +1 down
    public double Seconds   { get; set; }                   // addPause
    public double Yards     { get; set; }                   // simplifyRoute tolerance
    public bool   Reverse   { get; set; }                   // trailToRoute: backtrack (walk the trail in reverse)
}

/// <summary>Bridge payload for the engine-side Avalonia SettingsPanel.</summary>
public sealed class SettingsBridgePayload
{
    // Display
    public bool ShowTargetStaminaMana { get; set; }

    // UI
    public bool SuppressRetailRadar { get; set; }
    public bool ShowRynthRadar { get; set; }
    public bool RadarClickThrough { get; set; }
    public bool ShowRynthChat { get; set; }
    public bool ChatClickThrough { get; set; }
    public bool SuppressRetailPowerbar { get; set; }

    // Misc
    public bool EnableFPSLimit { get; set; }
    public int TargetFPSFocused { get; set; }
    public int TargetFPSBackground { get; set; }
    public bool EnableAutocram { get; set; }
    /// <summary>RynthSort's auto-sort is on: the engine greys out Auto Cram (sent, never read back).</summary>
    public bool AutoSortOn { get; set; }
    public bool PeaceModeWhenIdle { get; set; }
    // Optional: an engine without the row doesn't send it, and that must not turn it off.
    public bool? RearmAfterVitals { get; set; }
    public bool StartMacroOnLogin { get; set; }
    public bool PatrolOnLogin { get; set; }
    // Resume Meta After Reload. Nullable: an engine without the row doesn't send it (unchanged).
    public bool? ResumeMetaOnReload { get; set; }
    // Dungeon patrol. Nullable: an engine that predates them doesn't send them, and its save
    // must leave them alone.
    public int? DunPatrolCoverage { get; set; }
    public int? DunPatrolSkipDeadEnds { get; set; }
    public bool? DunPatrolAvoidFloors { get; set; }
    public float? DunPatrolWallDistance { get; set; }
    public float? DunPatrolLookInSize { get; set; }
    public float? DunPatrolSightYd { get; set; }
    // Outdoor hunt. Nullable: an engine that predates them doesn't send them.
    public float? OutdoorHuntRange { get; set; }
    public float? OutdoorHuntRadius { get; set; }
    public int? OutdoorHuntPick { get; set; }   // 0 Nearest, 1 Biggest pack, 2 Most XP (2026-11-08)
    // Loot by name. Nullable: an engine that predates them doesn't send them.
    public string? LootByNames { get; set; }
    public float? LootByNameRadius { get; set; }
    // Absent from an older engine's payload: must not read as off.
    public bool YieldToVTank { get; set; } = true;
    public bool EnableRaycasting { get; set; } = true;   // on unless the player turns it off (2026-11-05)
    public bool UseArcs { get; set; }
    public float BowArcVelocity { get; set; }
    public float CrossbowArcVelocity { get; set; }
    public float AtlatlArcVelocity { get; set; }
    public float MagicArcVelocity { get; set; }
    public float MissileArcClearance { get; set; } = 0.5f;   // absent in older payloads
    public bool LosDebugLog { get; set; }
    public int BlacklistAttempts { get; set; }
    public int BlacklistTimeoutSec { get; set; }
    // -1 = absent from the payload: the overlay's Settings panel didn't send it, and
    // applying the missing 0 made every panel click zero the setting (2026-09-27).
    public int BlacklistCastSettleMs { get; set; } = -1;
    public int TargetNoProgressTimeoutSec { get; set; }
    public int GiveQueueIntervalMs { get; set; }
    public int? T11ItemAugsOverride { get; set; }

    // Recharge
    public int HealAt { get; set; }
    public bool UsePotions { get; set; }
    public bool UseBuffItems { get; set; } = true;
    public int MakeRationsBelow { get; set; } = 5;
    public bool UseKitsInMagicMode { get; set; }
    public bool PeaceModeForKits { get; set; }
    public int KitMinSuccessPct { get; set; } = 70;
    public int StaminaKitMinSuccessPct { get; set; } = 70;   // an engine that doesn't know it keeps 70
    public int EmergencyHealAt { get; set; } = 30;
    public int StaminaToHealthAt { get; set; } = 30;
    public int StaminaToHealthMinStamina { get; set; } = 20;
    public bool StopMacroOnDeath { get; set; }
    public bool StopMacroOnNoComponents { get; set; }
    // Absent from an older engine: null keeps the plugin's values.
    public bool? LowCompsRecall { get; set; }
    public int? LowCompsTaperMin { get; set; }
    public int? LowCompsScarabMin { get; set; }
    public int? DeathRecallMode { get; set; }
    public int? DeathRecallSpellId { get; set; }
    public string? DeathRecallItem { get; set; }
    public bool StopLootingWhenPackFull { get; set; }
    public bool StopMacroWhenPackFull { get; set; }
    public int RestamAt { get; set; }
    public int? RestamTo { get; set; }
    public int GetManaAt { get; set; }
    public int TopOffHP { get; set; }
    public int TopOffStam { get; set; }
    public int TopOffMana { get; set; }
    public int HealOthersAt { get; set; }
    public int RestamOthersAt { get; set; }
    public int InfuseOthersAt { get; set; }

    // Melee Combat
    public bool UseRecklessness { get; set; }
    public int MeleeAttackPower { get; set; }
    public int MeleeAttackHeight { get; set; }
    public int MissileAttackPower { get; set; }
    public int MissileAttackHeight { get; set; }
    public bool UseNativeAttack { get; set; }
    public bool SummonPets { get; set; }
    public int PetMinMonsters { get; set; }
    // Variable attack power and the environment-impact throttle (2026-10-12). Null = sent by an
    // engine without these controls, which leaves the plugin's own values alone.
    public int? VariablePowerMin { get; set; }
    public int? VariablePowerMax { get; set; }
    public int? MissileEnvironmentResponse { get; set; }
    public int? MagicEnvironmentResponse { get; set; }
    public int? EnvironmentImpactLimit { get; set; }
    public int? EnvironmentImpactWindowSec { get; set; }
    public int? EnvironmentApproachMeters { get; set; }
    public int? EnvironmentMinDistance { get; set; }
    public int? EnvironmentSkipSec { get; set; }
    // Pull-away kite (2026-10-16). Null = sender without these controls; leave plugin values alone.
    public int? KitePullMeters { get; set; }
    public bool? KiteTagFirst { get; set; }
    // Weapon swaps (only when the sender includes them).
    public int? WeaponSwapSettleMs { get; set; }
    public int? WeaponSwapMinHealthPct { get; set; }
    public int? WeaponSwapMinGainPct { get; set; }

    // Spell Combat
    public int SpellCastIntervalMs { get; set; }
    // Nullable since 0 became a real value (2026-10-08): a file without it keeps the default.
    public int? AttackSpellIntervalMs { get; set; }
    public bool CastDispelSelf { get; set; }
    // VTank's options page (the engine's Classic dashboard, Main tab; 2026-10-19). Nullable: an engine
    // without them sends none, and the saved values stay.
    public bool? UseDispelItems { get; set; }
    public bool? AutoFellowMgmt { get; set; }
    public bool? OpenFellowOnCreate { get; set; }   // 2026-11-04: null from an older engine leaves it alone
    public bool? MChargesWhenOff { get; set; }
    public int? CustomPetRange { get; set; }
    public int MinRingTargets { get; set; }
    public int BlastRange { get; set; }
    public int MinBlastTargets { get; set; } = 3;   // absent from an older engine: keep the default
    // Walls (2026-10-08): null = sent by an engine without the Wall Range control, which then
    // leaves the plugin's own values alone (a profile's or a meta's) instead of zeroing them.
    public int? WallRange { get; set; }
    public int? MinWallTargets { get; set; }
    public int MinSkillLevelTier1 { get; set; }
    public int MinSkillLevelTier2 { get; set; }
    public int MinSkillLevelTier3 { get; set; }
    public int MinSkillLevelTier4 { get; set; }
    public int MinSkillLevelTier5 { get; set; }
    public int MinSkillLevelTier6 { get; set; }
    public int MinSkillLevelTier7 { get; set; }
    public int MinSkillLevelTier8 { get; set; }

    // Ranges
    public int MonsterRange { get; set; }
    public int MonsterDisengageRange { get; set; } = -1;   // -1 = absent, see BlacklistCastSettleMs
    // Nullable: an engine that predates them doesn't send them, and its save must leave them alone.
    public int? TargetSelection { get; set; }
    public bool? TargetLock { get; set; }
    public bool? FightSelectedTarget { get; set; }
    public int RingRange { get; set; }
    public int ApproachRange { get; set; }
    public double CorpseApproachRangeMax { get; set; }
    public double CorpseApproachRangeMin { get; set; }

    // Navigation
    public bool BoostNavPriority { get; set; }
    /// <summary>
    /// Options > Navigation > Vaulted Dungeons: RynthILT's keeper master switch, mirrored (RynthILT.Hub
    /// GetSetting / SetSetting). Null when no server plugin offers it (no RynthILT, or off ILT): the
    /// engine row then hides. Received: absent = unchanged; a value goes to RynthILT.
    /// </summary>
    public bool? EnableVaultedDungeons { get; set; }
    public float FollowNavMin { get; set; }
    public float NavRingThickness { get; set; }
    public float NavLineThickness { get; set; }
    public float NavHeightOffset { get; set; }
    public float NavSlopeSink { get; set; }
    public bool ShowTerrainPassability { get; set; }
    // Absent from an older engine: -1 / null keep the plugin's value.
    public int NavLabelMode { get; set; } = -1;
    public bool? NavGuideLine { get; set; }
    public bool? NavResumeNearest { get; set; }
    // NavOverlay.TrackBreadcrumbs / ShowRouteMarkers: the Nav panel's "Breadcrumbs" and "Route
    // overlay" switches, also on Settings > Navigation. Null (an older engine) = unchanged.
    public bool? TrackBreadcrumbs { get; set; }
    public bool? ShowRouteOverlay { get; set; }
    public bool OpenDoors { get; set; }
    public float OpenDoorRange { get; set; }
    public bool? OpenDoorsOnPathOnly { get; set; }   // absent (an older profile or plugin) = unchanged
    public bool AutoUnlockDoors { get; set; }
    public int MovementMode { get; set; }
    public float NavStopTurnAngle { get; set; }
    public float NavResumeTurnAngle { get; set; }
    public float NavDeadZone { get; set; }
    public float NavSweepMult { get; set; }
    public float NavLookaheadYards { get; set; }
    public float NavShortcutYards { get; set; }
    public float NavTurnRateDegPerSec { get; set; }
    public float NavTier1TurnSpeed { get; set; }
    public float PostPortalDelaySec { get; set; }
    public float T2Speed { get; set; }
    public float T2WalkWithinYd { get; set; }
    public float T2DistanceTo { get; set; }
    public float T2ReissueMs { get; set; }
    public float T2MaxRangeYd { get; set; }
    public int T2MaxLandblocks { get; set; }

    // Buffing
    public bool EnableBuffing { get; set; }
    public bool RebuffWhenIdle { get; set; }
    public int RebuffSecondsRemaining { get; set; }
    public int RebuffTopOffSecondsRemaining { get; set; } = 1200;   // absent in older files
    // Nullable: the engine's Settings face doesn't know it; absent = unchanged (0 is a real value).
    public int? RebuffIdleMarginMinutes { get; set; }
    // Nullable: an engine that predates the setting doesn't send it, and a save from its
    // Settings face must leave the setting alone rather than force it either way.
    public bool? CastBuffsOverItemBuffs { get; set; }
    public bool? SkipUnusableBuffs { get; set; }
    // Null (absent) likewise leaves the unticked prots/banes alone; an empty list ticks them all.
    public List<string>? DisabledBuffs { get; set; }
    public int BuffMinSkillLevelTier1 { get; set; }
    public int BuffMinSkillLevelTier2 { get; set; }
    public int BuffMinSkillLevelTier3 { get; set; }
    public int BuffMinSkillLevelTier4 { get; set; }
    public int BuffMinSkillLevelTier5 { get; set; }
    public int BuffMinSkillLevelTier6 { get; set; }
    public int BuffMinSkillLevelTier7 { get; set; }
    public int BuffMinSkillLevelTier8 { get; set; }

    // Crafting (read-only state)
    public bool EnableMissileCrafting { get; set; }
    public string MissileCraftingState { get; set; } = string.Empty;
    public bool MissileCraftingActive { get; set; }
    public string MissileCraftingStatus { get; set; } = string.Empty;

    // Looting
    public bool EnableLooting { get; set; }
    public bool BoostLootPriority { get; set; }
    public bool LootOnlyRareCorpses { get; set; }
    public bool LootJumpEnabled { get; set; }
    public bool LootChestsMacroOff { get; set; } = true;   // an engine that doesn't know it leaves it on
    public bool LootChestsMacroOffLeaveOpen { get; set; }   // off: an engine that doesn't know it closes, as before
    public int? ChestRelockWaitSec { get; set; }           // Item Tool's re-lock wait; absent (the engine has no row for it) = unchanged
    public int LootJumpHeight { get; set; }
    public int LootOwnership { get; set; }
    // Nullable, as CastBuffsOverItemBuffs: an engine that predates them doesn't send them,
    // and its save must leave them alone.
    public bool? LootOwnCorpse { get; set; }
    public bool? EscapeHazards { get; set; }
    public bool? TravelToOwnCorpse { get; set; }
    public int? LootMode { get; set; }
    // Item info: describe an item in chat when it is selected (ItemInfoSettings.OnSelect).
    public bool? ItemInfoOnSelect { get; set; }
    public bool EnableAutostack { get; set; }
    public bool ReadUnknownScrolls { get; set; } = true;   // absent from an older engine: keep the default
    public bool? UnboxAetheriaWhenIdle { get; set; }        // absent from an older engine: unchanged
    public bool EnableCombineSalvage { get; set; }
    public bool CombineBagsDuringSalvage { get; set; }
    public bool? SalvageWithoutPanel { get; set; }   // absent from an older engine: unchanged
    public int LootInterItemDelayMs { get; set; }
    public int LootContentSettleMs { get; set; }
    public int LootEmptyCorpseMs { get; set; }
    public int LootClosingDelayMs { get; set; }
    public int LootAssessWindowMs { get; set; }
    public int LootRetryTimeoutMs { get; set; }
    public int LootOpenRetryMs { get; set; }
    public int LootCorpseTimeoutMs { get; set; }
    public int SalvageOpenDelayFirstMs { get; set; }
    public int SalvageOpenDelayFastMs { get; set; }
    public int SalvageAddDelayFirstMs { get; set; }
    public int SalvageAddDelayFastMs { get; set; }
    public int SalvageSalvageDelayMs { get; set; }
    public int SalvageResultDelayFirstMs { get; set; }
    public int SalvageResultDelayFastMs { get; set; }
    // AutoVendor. Nullable so a settings panel that doesn't know these fields yet
    // leaves the character's values alone instead of resetting them.
    public bool? AutoVendorEnabled { get; set; }
    public bool? AutoVendorEnableBuying { get; set; }
    public bool? AutoVendorEnableSelling { get; set; }
    public bool? AutoVendorTestMode { get; set; }
    public bool? AutoVendorThink { get; set; }
    public bool? AutoVendorShowMerchantInfo { get; set; }
    public bool? AutoVendorOnlyFromMainPack { get; set; }
    public int? AutoVendorTries { get; set; }
    public int? AutoVendorTriesTime { get; set; }
    // The off hand (only when the sender includes them; today's engine Settings face doesn't).
    public string? OffhandDefault { get; set; }
    public bool? PreferDualWield { get; set; }

    // Ground loot (GroundLootController; also /ra groundloot on|off).
    public bool EnableGroundLoot { get; set; }

    // Diagnostics. Stored per PC in Logs\Diagnostics\diagnostics.json (RynthLog), not in
    // the character profile; applied only when a value differs, so a save of other
    // settings doesn't rewrite that file.
    public bool DiagDebugToChat { get; set; }
    public bool DiagFileLogAll { get; set; } = true;
    /// <summary>Every trace category as "Name=Level" joined by ','; Level 0 = Off, 1 = Trace, 2 = Info.</summary>
    public string DiagCategories { get; set; } = string.Empty;
    /// <summary>Read-only: the diagnostics folder, shown under the switches.</summary>
    public string DiagFolder { get; set; } = string.Empty;
}

public enum MetaConditionType
{
    Never,
    Always,
    All,
    Any,
    ChatMessage,
    PackSlots_LE,
    SecondsInState_GE,
    CharacterDeath,
    AnyVendorOpen,
    VendorClosed,
    InventoryItemCount_LE,
    InventoryItemCount_GE,
    MonsterNameCountWithinDistance,
    MonsterPriorityCountWithinDistance,
    NeedToBuff,
    NoMonstersWithinDistance,
    Landblock_EQ,
    Landcell_EQ,
    PortalspaceEntered,
    PortalspaceExited,
    Not,
    SecondsInStateP_GE,
    TimeLeftOnSpell_GE,
    TimeLeftOnSpell_LE,
    BurdenPercentage_GE,
    DistAnyRoutePT_GE,
    Expression,
    ChatMessageCapture,
    NavrouteEmpty,
    MainHealthLE,
    MainHealthPHE,
    MainManaLE,
    MainManaPHE,
    MainStamLE,
    VitaePHE,
    /// <summary>RynthAi only (2026-10-06): no kill for at least N minutes (ConditionData = minutes,
    /// decimals allowed). Appended last: the value is the wire / JSON number, and an older RynthAi
    /// that reads it treats the unknown number (and the .af keyword NoKillsInMins) as never true.</summary>
    NoKillsWithinMinutes
}

public enum MetaActionType
{
    None,
    ChatCommand,
    SetMetaState,
    EmbeddedNavRoute,
    All,
    CallMetaState,
    ReturnFromCall,
    ExpressionAction,
    ChatExpression,
    SetWatchdog,
    ClearWatchdog,
    GetRAOption,
    SetRAOption,
    CreateView,
    DestroyView,
    DestroyAllViews
}

public sealed class MetaRule
{
    public string State { get; set; } = "Default";
    public MetaConditionType Condition { get; set; }
    public string ConditionData { get; set; } = string.Empty;
    public MetaActionType Action { get; set; }
    public string ActionData { get; set; } = string.Empty;
    public List<MetaRule> Children { get; set; } = new();
    public List<MetaRule> ActionChildren { get; set; } = new();

    /// <summary>CallMetaState only: the state pushed on the call stack (VTank's
    /// "ret"). Empty means the calling rule's current state, as before.</summary>
    public string CallReturnState { get; set; } = string.Empty;

    /// <summary>ChatMessageCapture only: VTank's colour (chat type) id list,
    /// semicolon-separated ("2;4"). Empty matches every chat type.</summary>
    public string ChatColors { get; set; } = string.Empty;

    /// <summary>Disabled rules are excluded from the per-state index so they
    /// never evaluate. Default true; missing in old JSON/.af → stays true
    /// (the property initializer runs, so backward-compatible).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>A name to find the rule by in the Meta panel (optional; only top-level rules have
    /// one). Saved in .af as a "~~ @name ..." line before the rule's IF: (a plain comment to
    /// other tools, like "~~ @disabled"); .met has no place for it.</summary>
    public string Name { get; set; } = string.Empty;

    [JsonIgnore]
    public bool HasFired { get; set; }

    /// <summary>Last time this rule's action executed — drives the red-flash UI.</summary>
    [JsonIgnore]
    public DateTime LastFiredAt { get; set; } = DateTime.MinValue;
}

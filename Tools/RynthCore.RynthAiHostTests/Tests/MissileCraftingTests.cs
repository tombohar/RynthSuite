using System;
using System.Linq;
using System.Reflection;
using RynthCore.Plugin.RynthAi;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.Plugin.Shared;
using RynthCore.PluginSdk;
using RynthCore.RynthAiHostTests.Fakes;
using RynthCore.RynthAiTests.Harness;

namespace RynthCore.RynthAiHostTests.Tests;

// Silentkelpie, 2026-10-15: with Missile Crafting on, combat swapped Electric Bow + arrows for
// Fire Crossbow + quarrels, and crafting kept wielding 'Infinite Deadly Prismatic Arrow' every
// 5 s (784 tries in one session). The stowed bow still read wielded (the client keeps a stowed
// item's old wielder and slot), the crossbow in hand read slot 0, the server ignored each arrow
// wield, and every try stopped the nav route while it waited.
internal static class MissileCraftingTests
{
    private const uint Player = 0x50000C01;
    private const uint Crossbow = 0x80000601, Bow = 0x80000602, Quarrels = 0x80000603, Arrows = 0x80000604;
    private const uint Heads = 0x80000605, Shafts = 0x80000606, Made = 0x80000607;
    private const uint StackSizeKey = 12;
    private const int MissileSlot = 0x00400000, AmmoSlot = 0x00800000;

    public static void Register(Runner r)
    {
        r.Add("missile crafting: a stowed bow that still reads wielded is not the launcher (no arrows at a crossbow)", StowedBowNotLauncher);
        r.Add("missile crafting: wielded quarrels are not taken for a bow launcher", AmmoNotLauncher);
        r.Add("missile crafting: an ammo wield the server ignores backs off after 2 tries", IgnoredWieldBacksOff);
        r.Add("missile crafting: a bow in hand with its arrows in the pack still gets them wielded", BowInHandArrowsInPack);
        r.Add("missile crafting: in a fight the carried arrows go on at once, better ones are not crafted (no Peace, no combine)", FightWieldsCarried);
        r.Add("missile crafting: a craft of better arrows stops when a fight starts, and the carried arrows go on", FightStopsUpgrade);
        r.Add("missile crafting: out of arrows in a fight, one combine still goes (nothing else to shoot), then the arrows made go on", FightOutOfAmmoCraftsOnce);
        // The engine's real container walk (2026-10-21): the player's own contents list the gear in hand.
        r.Add("missile crafting: the walk lists the bow in hand under the player, and Deadly Prismatic Arrows are still made and wielded", WalkBowInHandCrafts);
        r.Add("missile crafting: with the engine's tracker, a bow in hand whose client fields read blank is the launcher", WalkTrackerBowInHand);
        r.Add("missile crafting: with the engine's tracker, a stowed bow listed first that still reads wielded is not the launcher", WalkTrackerStowedBowNotLauncher);
        r.Add("missile crafting: a stowed bow in a side pack that still reads wielded is not the launcher (no tracker)", WalkSidePackBowNotLauncher);
    }

    /// <summary>Kelpie's state at 13:36: crossbow and quarrels in hand (slot 0), bow and arrows in the main pack.</summary>
    private static void StowedBowNotLauncher()
    {
        var cm = Rig(crossbowInHand: true);
        Idle(cm);
        Check.Eq(cm.State, MissileCraftingManager.CraftState.Idle, "crossbow with quarrels on: nothing to do");
        Check.Eq(FakeHost.Uses.Count, 0, "no arrow wield");
        Check.False(FakeHost.Logs.Any(l => l.Contains("MissileCraft: no")), "no evaluation logged");
    }

    /// <summary>The quarrels wielded at login (slot set, typed MissileWeapon) and no launcher readable.</summary>
    private static void AmmoNotLauncher()
    {
        var cm = Rig(crossbowInHand: false, arrange: () =>
        {
            FakeHost.Containers.Remove(Bow);              // no bow anywhere
            FakeHost.Wielded.Remove(Bow);
            FakeHost.Containers.Remove(Quarrels);         // the quarrels on, the crossbow in the pack
            FakeHost.Wielded[Quarrels] = AmmoSlot;
        });
        Idle(cm);
        Check.Eq(cm.State, MissileCraftingManager.CraftState.Idle, "no launcher: crafting idles");
        Check.Eq(FakeHost.Uses.Count, 0, "the quarrels never pass for a bow");
    }

    /// <summary>Bow in hand, arrows in the pack, and the server never puts them on.</summary>
    private static void IgnoredWieldBacksOff()
    {
        var cm = BowRig();
        for (int i = 0; i < 4; i++) Cycle(cm);
        Check.Eq(FakeHost.Uses.Count(u => u == Arrows), 2, "two arrow wields, then no more");
        Check.Eq(cm.State, MissileCraftingManager.CraftState.Idle, "crafting let go");
        Check.True(FakeHost.Logs.Any(l => l.Contains("didn't wield") && l.Contains("2 tries")), "the back-off is logged");

        Cycle(cm);
        Check.Eq(FakeHost.Uses.Count(u => u == Arrows), 2, "inside the back-off: still two");
    }

    /// <summary>The fix must not stop crafting doing its job: no ammo on, the pack's arrows go on.</summary>
    private static void BowInHandArrowsInPack()
    {
        var cm = BowRig();
        Cycle(cm);
        Check.Eq(FakeHost.Uses.LastOrDefault(), Arrows, "UseObject(arrows)");
        Check.True(FakeHost.Logs.Any(l => l.Contains("MissileCraft: no Arrow") && l.Contains("'Longbow'")), "the launcher it saw is logged");
    }

    // ── Fights (structural audit 2026-10-21, finding 3) ──────────────────────
    // Crafting holds combat (Peace mode, up to 15 s a step) and had no danger check: the arrows run
    // out mid-fight, which is when it starts. In a fight it now does only what the fight needs.

    /// <summary>Longbow in hand, nothing in the ammo slot, the Infinite arrows (no recipe: rank 1) in the pack, and bundles for Greater Arrows.</summary>
    private static MissileCraftingManager UpgradeRig(Func<bool> fight, bool carriedArrows = true) =>
        Rig(crossbowInHand: false, extra: new[] { Heads, Shafts }, arrange: () =>
        {
            FakeHost.Containers.Remove(Bow);
            FakeHost.Names[Bow] = "Longbow";
            if (!carriedArrows) { FakeHost.Containers.Remove(Arrows); FakeHost.Names[Arrows] = "Infinite Deadly Prismatic Quarrel"; }
            FakeHost.UseOnCalls = true;
            FakeHost.CombatModeCalls = true;
            FakeHost.CombatModeValue = CombatMode.Missile;
            Bundle(Heads, "Bundle of Greater Arrowheads");
            Bundle(Shafts, "Bundle of Arrowshafts");
        }, fight: fight);

    private static void Bundle(uint id, string name)
    {
        FakeHost.Names[id] = name;
        FakeHost.ItemTypes[id] = 0x100;
        FakeHost.Containers[id] = Player;
        FakeHost.Ints[(id, StackSizeKey)] = 2;
    }

    private static void FightWieldsCarried()
    {
        // Out of a fight (the old behaviour): the better arrows are crafted.
        var quiet = UpgradeRig(() => false);
        Idle(quiet);
        Step(quiet);
        Check.Eq(quiet.State, MissileCraftingManager.CraftState.Combining, "no fight: Greater Arrows are crafted");

        var cm = UpgradeRig(() => true);
        Cycle(cm);
        Check.Eq(FakeHost.Uses.LastOrDefault(), Arrows, "in a fight: the carried arrows are wielded");
        Check.Eq(FakeHost.UsesOn.Count, 0, "no combine");
        Check.Eq(FakeHost.CombatModeRequests.Count, 0, "no Peace mode");
        Check.Eq(cm.State, MissileCraftingManager.CraftState.Idle, "and crafting let go");
        Check.True(FakeHost.Chat.Any(l => l.Contains("In a fight: wielding Infinite Deadly Prismatic Arrow now instead of crafting Greater Arrow")), "chat says why");
    }

    private static void FightStopsUpgrade()
    {
        bool fight = false;
        var cm = UpgradeRig(() => fight);
        Idle(cm);
        Step(cm);   // evaluating: the craft starts
        Step(cm);   // the first combine goes out
        Check.Eq(FakeHost.UsesOn.Count, 1, "the craft is under way");
        Check.Eq(FakeHost.CombatModeRequests.LastOrDefault(), CombatMode.NonCombat, "in Peace mode");

        fight = true;
        Step(cm);
        Check.Eq(cm.State, MissileCraftingManager.CraftState.EquippingAmmo, "a fight: the craft stops");
        Step(cm);
        Check.Eq(FakeHost.Uses.LastOrDefault(), Arrows, "the carried arrows go on");
        Check.Eq(cm.State, MissileCraftingManager.CraftState.Idle, "crafting let go of the bot");
        Check.Eq(FakeHost.UsesOn.Count, 1, "no second combine");
        Check.True(FakeHost.Chat.Any(l => l.Contains("In a fight: stopped crafting Greater Arrow")), "chat says so");
    }

    private static void FightOutOfAmmoCraftsOnce()
    {
        var cm = UpgradeRig(() => true, carriedArrows: false);
        Idle(cm);
        Step(cm);
        Step(cm);
        Check.Eq(FakeHost.UsesOn.Count, 1, "no arrows at all: the first combine goes, fight or not");

        // The server makes the arrows.
        FakeHost.Names[Made] = "Greater Arrow";
        FakeHost.ItemTypes[Made] = 0x100;
        FakeHost.Containers[Made] = Player;
        _ = _cache![unchecked((int)Made)];
        cm.HandleChat("You make 2 Greater Arrows.");
        Step(cm);
        Check.Eq(cm.State, MissileCraftingManager.CraftState.EquippingAmmo, "arrows to shoot now: the second combine waits");
        Step(cm);
        Check.Eq(FakeHost.Uses.LastOrDefault(), Made, "the arrows made go on");
        Check.Eq(FakeHost.UsesOn.Count, 1, "one combine in the fight");
    }

    /// <summary>One crafting tick, the phase clock moved past EQUIP_DELAY_MS (inside the 15 s timeout).</summary>
    private static void Step(MissileCraftingManager cm)
    {
        PhaseStart.SetValue(cm, DateTime.Now.AddSeconds(-1));
        ForceRescan();
        cm.ProcessCrafting();
    }

    // ── The engine's real container walk ───────────────────────────────────
    //
    // 2026-10-21, a player: "it doesn't fletch arrows, even when the option is checked" (Deadly
    // Prismatic). The engine's GetContainerContents(player) lists what the player wields as well as
    // what the main pack holds (UpdateObjectInventoryHooks.ScanByContainerId matches the wielder too),
    // so the walk filed the bow in hand under the player. Since 2026-10-19 crafting took any item the
    // walk filed under a container as "not in hand", saw no launcher, and never started, with nothing
    // in chat or the log. The rig above has no GetContainerContents, so its tests never met the walk.

    private const uint SidePack = 0x80000610, WalkHeads = 0x80000611, WalkShafts = 0x80000612, WalkMade = 0x80000613;
    private const uint ContainerType = 0x200;   // ITEM_TYPE container: the walk opens it

    /// <summary>The engine's walk as it is: wielded gear in the player's own contents, with no container.</summary>
    private static MissileCraftingManager WalkRig(bool tracker, Action arrange, int fletching = 470)
    {
        PaceOff();
        FakeHost.Reset();
        RynthCore.Plugin.RynthAi.EquippedState.Invalidate();
        FakeHost.WeaponCalls = true;       // UseObject, GetCurrentCombatMode (peace: 1)
        FakeHost.SalvageCalls = true;      // GetObjectWielderInfo (answers from Wielded)
        FakeHost.ContentsCalls = true;     // GetContainerContents (Containers, then ContentsExtra)
        FakeHost.UseOnCalls = true;        // UseObjectOn (the head bundle on the shaft bundle)
        FakeHost.EquippedCalls = tracker;  // v87 GetWieldedItems (the engine's EquipmentTracker)
        FakeHost.Names[Player] = "Archer";
        FakeHost.Skills[(Player, 37)] = (fletching, 3);   // Fletching, specialized
        Item(Crossbow, "Fire Crossbow", MissileSlot);
        Item(Bow, "Longbow", MissileSlot);
        Item(Quarrels, "Deadly Prismatic Quarrel", AmmoSlot);
        Item(Arrows, "Deadly Prismatic Arrow", AmmoSlot);
        FakeHost.Names[WalkHeads] = "Bundle of Deadly Prismatic Arrowheads";
        FakeHost.Names[WalkShafts] = "Bundle of Arrowshafts";
        FakeHost.Ints[(WalkHeads, 12)] = 10;   // StackCount
        FakeHost.Ints[(WalkShafts, 12)] = 10;
        FakeHost.Containers[WalkHeads] = Player;
        FakeHost.Containers[WalkShafts] = Player;
        arrange();

        var host = FakeHost.Create(Player);
        _cache = FakeHost.MakeCache(host, Player, new[] { Crossbow, Bow, Quarrels, Arrows, WalkHeads, WalkShafts, SidePack });
        var cm = new MissileCraftingManager(host, new LegacyUiSettings { EnableMissileCrafting = true });
        cm.SetObjectCache(_cache);
        var skills = new CharacterSkills(host);
        skills.SetPlayerId(Player);
        cm.SetCharacterSkills(skills);
        return cm;
    }

    /// <summary>In hand as the client shows it: the record names the player, no container, and the
    /// walk lists it in the player's contents. The tracker (when on) says so too.</summary>
    private static void InHand(uint id, int slot, bool clientSlot = true, bool tracker = false)
    {
        if (clientSlot) FakeHost.Wielded[id] = unchecked((uint)slot);
        FakeHost.Wielders[id] = Player;
        if (!FakeHost.ContentsExtra.TryGetValue(Player, out var extra))
            FakeHost.ContentsExtra[Player] = extra = new();
        extra.Add(id);
        if (tracker) FakeHost.Equipped[id] = unchecked((uint)slot);
    }

    /// <summary>One tick of whatever state crafting is in, its waits already over.</summary>
    private static void WalkStep(MissileCraftingManager cm)
    {
        if (cm.State == MissileCraftingManager.CraftState.Idle) LastAmmoCheck.SetValue(cm, DateTime.MinValue);
        else PhaseStart.SetValue(cm, DateTime.Now.AddSeconds(-1));   // past EQUIP_DELAY_MS, well inside the 15 s timeout
        ForceRescan();
        cm.ProcessCrafting();
    }

    /// <summary>The player's case: a Longbow wielded at login, the quiver empty, Deadly Prismatic bundles in the pack.</summary>
    private static void WalkBowInHandCrafts()
    {
        var cm = WalkRig(tracker: false, arrange: () =>
        {
            InHand(Bow, MissileSlot);
            FakeHost.Names.Remove(Crossbow); FakeHost.Names.Remove(Quarrels); FakeHost.Names.Remove(Arrows);
        });

        WalkStep(cm);
        Check.Eq(cm.State, MissileCraftingManager.CraftState.Evaluating, "the empty quiver is seen");
        Check.True(FakeHost.Logs.Any(l => l.Contains("MissileCraft: no Arrow") && l.Contains("'Longbow'")), "the bow in hand is the launcher");

        WalkStep(cm);
        Check.Eq(cm.State, MissileCraftingManager.CraftState.Combining, "Deadly Prismatic Arrows are made (Fletching 470)");
        Check.True(FakeHost.Chat.Any(c => c.Contains("Crafting Deadly Prismatic Arrow x2")), "two combines planned: " + string.Join(" | ", FakeHost.Chat));

        WalkStep(cm);
        Check.Eq(FakeHost.UsesOn.Count, 1, "one combine sent");
        Check.Eq(FakeHost.UsesOn[0], (WalkHeads, WalkShafts), "the head bundle on the shaft bundle");

        cm.HandleChat("You make a bundle of deadly prismatic arrows.");
        WalkStep(cm);
        Check.Eq(FakeHost.UsesOn.Count, 2, "the second combine goes out after the first is confirmed");
        cm.HandleChat("You make a bundle of deadly prismatic arrows.");

        // The new arrows land in the main pack.
        Item(WalkMade, "Deadly Prismatic Arrow", AmmoSlot);
        FakeHost.Containers[WalkMade] = Player;
        WalkStep(cm);
        Check.Eq(cm.State, MissileCraftingManager.CraftState.EquippingAmmo, "both combines done: equip");
        WalkStep(cm);
        Check.Eq(FakeHost.Uses.LastOrDefault(), WalkMade, "UseObject(the new arrows)");
        Check.Eq(cm.State, MissileCraftingManager.CraftState.Idle, "done");
    }

    /// <summary>The bow wielded after login: the client's slot and wielder record read blank; the tracker knows.</summary>
    private static void WalkTrackerBowInHand()
    {
        var cm = WalkRig(tracker: true, arrange: () =>
        {
            FakeHost.ContentsExtra[Player] = new() { Bow };   // listed, but the record says nothing
            FakeHost.Equipped[Bow] = MissileSlot;              // the server's WieldObject
            FakeHost.Names.Remove(Crossbow); FakeHost.Names.Remove(Quarrels); FakeHost.Names.Remove(Arrows);
        });

        WalkStep(cm);
        Check.Eq(cm.State, MissileCraftingManager.CraftState.Evaluating, "the tracker's bow is the launcher");
        WalkStep(cm);
        WalkStep(cm);
        Check.Eq(FakeHost.UsesOn.Count, 1, "the combine goes out");
    }

    /// <summary>
    /// Silentkelpie's 2026-10-15 state on the real walk, with the tracker: crossbow and quarrels in
    /// hand, the bow and arrows put into the main pack, the bow's record still naming the player and
    /// the missile slot, and the bow listed before the crossbow. Only the tracker can tell them apart.
    /// </summary>
    private static void WalkTrackerStowedBowNotLauncher()
    {
        var cm = WalkRig(tracker: true, arrange: () =>
        {
            FakeHost.Containers[Bow] = Player;              // in the main pack (listed first) ...
            FakeHost.Wielded[Bow] = MissileSlot;            // ... with the stale record
            FakeHost.Wielders[Bow] = Player;
            FakeHost.Containers[Arrows] = Player;
            InHand(Crossbow, MissileSlot, clientSlot: false, tracker: true);   // wielded after login: slot reads 0
            InHand(Quarrels, AmmoSlot, clientSlot: false, tracker: true);
        });

        for (int i = 0; i < 3; i++) WalkStep(cm);
        Check.Eq(cm.State, MissileCraftingManager.CraftState.Idle, "crossbow with quarrels on: nothing to do");
        Check.Eq(FakeHost.Uses.Count, 0, "no arrow wield at the crossbow");
        Check.Eq(FakeHost.UsesOn.Count, 0, "no combine");
        Check.False(FakeHost.Logs.Any(l => l.Contains("MissileCraft: no")), "no evaluation logged");
    }

    /// <summary>
    /// Without the tracker: the stowed bow sits in a side pack with its stale record (wielder and
    /// missile slot), and the crossbow in hand can't be read (its record is blank, so the walk doesn't
    /// list it). The stale bow is the only candidate and must not pass for the launcher.
    /// </summary>
    private static void WalkSidePackBowNotLauncher()
    {
        var cm = WalkRig(tracker: false, arrange: () =>
        {
            FakeHost.Names[SidePack] = "Backpack";
            FakeHost.ItemTypes[SidePack] = ContainerType;
            FakeHost.Containers[SidePack] = Player;
            FakeHost.Containers[Bow] = SidePack;
            FakeHost.Wielded[Bow] = MissileSlot;            // stale
            FakeHost.Wielders[Bow] = Player;
            FakeHost.Containers[Arrows] = SidePack;
        });

        for (int i = 0; i < 3; i++) WalkStep(cm);
        Check.Eq(cm.State, MissileCraftingManager.CraftState.Idle, "no launcher it can trust: nothing to do");
        Check.Eq(FakeHost.Uses.Count, 0, "no arrow wield");
        Check.False(FakeHost.Logs.Any(l => l.Contains("MissileCraft: no")), "no evaluation logged");
    }


    // ── The rig ─────────────────────────────────────────────────────────────

    private static WorldObjectCache? _cache;

    /// <param name="arrange">Changes to the fake world, made before the host and cache read it.</param>
    private static MissileCraftingManager Rig(bool crossbowInHand, Action? arrange = null, uint[]? extra = null, Func<bool>? fight = null)
    {
        PaceOff();
        FakeHost.Reset();
        FakeHost.WeaponCalls = true;       // UseObject, GetCurrentCombatMode
        FakeHost.SalvageCalls = true;      // GetObjectWielderInfo (answers from Wielded)
        FakeHost.Names[Player] = "Silentkelpie";
        Item(Crossbow, "Fire Crossbow", MissileSlot);
        Item(Bow, "Electric Bow", MissileSlot);
        Item(Quarrels, "Infinite Deadly Prismatic Quarrel", AmmoSlot);
        Item(Arrows, "Infinite Deadly Prismatic Arrow", AmmoSlot);

        // The pack listing holds only what is in the pack; the stowed bow's description still
        // names the player and the missile slot.
        FakeHost.Containers[Bow] = Player;
        FakeHost.Containers[Arrows] = Player;
        FakeHost.Wielded[Bow] = MissileSlot;
        if (crossbowInHand)
        {
            FakeHost.Wielded[Crossbow] = 0;                 // wielded after login: slot reads 0
            FakeHost.Wielded[Quarrels] = 0;
        }
        else
        {
            FakeHost.Containers[Crossbow] = Player;
            FakeHost.Containers[Quarrels] = Player;
        }
        arrange?.Invoke();

        var host = FakeHost.Create(Player);
        _cache = FakeHost.MakeCache(host, Player, new[] { Crossbow, Bow, Quarrels, Arrows }.Concat(extra ?? Array.Empty<uint>()));
        var cm = new MissileCraftingManager(host, new LegacyUiSettings { EnableMissileCrafting = true });
        cm.SetObjectCache(_cache);
        cm.FightIsOn = fight;
        return cm;
    }

    /// <summary>A real bow in hand (slot set) with nothing in the ammo slot and arrows in the pack.</summary>
    private static MissileCraftingManager BowRig() => Rig(crossbowInHand: false, arrange: () =>
    {
        FakeHost.Containers.Remove(Bow);
        FakeHost.Names[Bow] = "Longbow";
    });

    private static void Item(uint id, string name, int validSlots)
    {
        FakeHost.Names[id] = name;
        FakeHost.ItemTypes[id] = 0x100;                    // MissileWeapon, as the server types ammo too
        FakeHost.Ints[(id, 9)] = validSlots;
    }

    private static readonly FieldInfo LastAmmoCheck =
        typeof(MissileCraftingManager).GetField("_lastAmmoCheck", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly FieldInfo PhaseStart =
        typeof(MissileCraftingManager).GetField("_phaseStart", BindingFlags.Instance | BindingFlags.NonPublic)!;

    /// <summary>One idle tick with the 5 s ammo-check interval already passed.</summary>
    private static void Idle(MissileCraftingManager cm)
    {
        LastAmmoCheck.SetValue(cm, DateTime.MinValue);
        ForceRescan();
        cm.ProcessCrafting();
    }

    /// <summary>One ammo check through to its wield (or its back-off), the clocks moved past each wait.</summary>
    private static void Cycle(MissileCraftingManager cm)
    {
        for (int i = 0; i < 4 && (i == 0 || cm.State != MissileCraftingManager.CraftState.Idle); i++)
        {
            if (cm.State == MissileCraftingManager.CraftState.Idle)
            {
                // The back-off moves _lastAmmoCheck into the future; only an expired check is reset.
                if ((DateTime)LastAmmoCheck.GetValue(cm)! <= DateTime.Now)
                    LastAmmoCheck.SetValue(cm, DateTime.MinValue);
            }
            else
            {
                PhaseStart.SetValue(cm, DateTime.Now.AddSeconds(-1));   // past EQUIP_DELAY_MS
            }
            ForceRescan();
            cm.ProcessCrafting();
        }
    }

    /// <summary>GetDirectInventory re-serves a scan under 1 s old; the test can't wait on the wall clock.
    /// The tracker read is cached for 100 ms across hosts, so it is dropped too.</summary>
    private static void ForceRescan()
    {
        typeof(WorldObjectCache).GetField("_lastDirectInventoryScan", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(_cache, DateTime.MinValue);
        RynthCore.Plugin.RynthAi.EquippedState.Invalidate();
    }

    private static void PaceOff()
    {
        ActionPacer.Enabled = false;
        ActionPacer.Reset();
        ActionPacer.NowMs = () => Environment.TickCount64;
        UseAudit.Reset("?", null);
        UseAudit.NowMs = () => Environment.TickCount64;
        MoveAudit.Reset();
        MoveAudit.NowMs = () => Environment.TickCount64;
    }
}

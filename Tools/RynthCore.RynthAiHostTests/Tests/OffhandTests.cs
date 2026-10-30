using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using RynthCore.Loot;
using RynthCore.Plugin.RynthAi;
using RynthCore.Plugin.RynthAi.CreatureData;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.RynthAiHostTests.Fakes;
using RynthCore.RynthAiTests.Harness;

namespace RynthCore.RynthAiHostTests.Tests;

// The off hand (2026-10-01): a listed shield with a one-handed weapon, dual wield with the Dual
// Wield skill, nothing with a two-hander / launcher / caster. ACE refuses a two-hander, a launcher
// or a caster while the Shield slot (0x00200000) is occupied, so the shield goes into a pack
// first; with Offhand None nothing in the off hand is ever touched. Listed items only, one action
// in flight, confirmed in the slot, a retry cap, no flip-flop.
internal static class OffhandTests
{
    private const uint Player = 0x50000C01;
    private const uint Monster = 0x80001001;
    private const uint Wcid = 434343;
    private const uint Sword = 0x80000401, Dagger = 0x80000402, Great = 0x80000403, Shield = 0x80000404,
                       Round = 0x80000405, Bow = 0x80000406, Arrows = 0x80000407, Wand = 0x80000408;
    private const uint Pack = 0x80000501;
    private const int MeleeSlot = 0x00100000, ShieldSlot = 0x00200000, MissileSlot = 0x00400000,
                      AmmoSlot = 0x00800000, HeldSlot = 0x01000000, TwoHandedSlot = 0x02000000;

    public static void Register(Runner r)
    {
        r.Add("offhand: ACE's rules and the plan (shield, dual wield, nothing for two-handers)", Rules);
        r.Add("offhand: one-hander + shield, wielded once the main weapon is in hand", OneHanderShield);
        r.Add("offhand: two-hander -> the shield comes off first, back on with a one-hander", TwoHanderShieldOff);
        r.Add("offhand: bow -> the shield comes off, then the arrows still go on", BowShieldOffThenAmmo);
        r.Add("offhand: a caster -> the shield comes off first (swap gate held)", CasterShieldOff);
        r.Add("swap speed: a weapon in hand -> one use of the wand (the client swaps); the pack move only as the fallback", CasterDirectSwap);
        r.Add("swap speed: the gate lets the next step go once the server is done, else after 3 s", SwapGateSettles);
        r.Add("offhand: dual wield with the Dual Wield skill", DualWield);
        r.Add("offhand: the weapon in hand is the main hand's when the wielded slot reads 0 (the weenie's wielder record)", InHandFromWielderInfo);
        r.Add("weapons: a weapon refused 'skill too low' is left out of the choice (the off hand stays)", SkillTooLowLeftOut);
        r.Add("offhand: two weapons wielded and no slot read says which: the one RynthAi put in the off hand is the off hand's", OffhandRemembered);
        r.Add("weapons: a weapon in a pack is not in hand, though its wielder record still says the player", PackedIsNotWielded);
        r.Add("weapons: the equip step wields a weapon whose stale wielder record says 'in hand' while it is in a pack", EquipIgnoresStaleRecord);
        r.Add("weapons: a wielded item the client lists in the player's own contents is still wielded", WieldedInOwnContentsIsWielded);
        r.Add("tracker (v87): the server's list decides what is in hand, whatever the client's stale fields say", TrackerDecidesInHand);
        r.Add("tracker (v87): the main hand and the off hand come from the server's slots", TrackerHands);
        r.Add("tracker (v87): not ready yet (-1): the old reads answer", TrackerNotReadyFallsBack);
        r.Add("weapons: a two-hander needing untrained Two Handed Combat is never chosen, so the off hand stays", UnmetRequirementNotChosen);
        r.Add("weapons: vitae doesn't leave out a weapon whose requirement is the base skill (the Shashqa at 40% vitae)", VitaeAndBaseSkillRequirement);
        r.Add("offhand: no Dual Wield skill -> the shield", NoSkillShield);
        r.Add("offhand: None -> hands off (a worn shield is never taken off)", NoneHandsOff);
        r.Add("offhand: an unlisted shield is never wielded", UnlistedNeverUsed);
        r.Add("offhand: no flip-flop, and a retry cap", NoFlipFlop);
        r.Add("offhand: the Items list (shields aren't weapons, no element) and buffing's wand", ItemsAndBuffing);
    }

    // ── Pure rules ────────────────────────────────────────────────────────────

    private static void Rules()
    {
        Check.True(OffhandRules.OffhandAllowed(MainHandKind.OneHandedMelee, true), "one-hander + shield");
        Check.True(OffhandRules.OffhandAllowed(MainHandKind.OneHandedMelee, false), "one-hander + off-hand weapon");
        Check.False(OffhandRules.OffhandAllowed(MainHandKind.TwoHanded, true), "two-hander: no shield");
        Check.False(OffhandRules.OffhandAllowed(MainHandKind.Launcher, true), "launcher: no shield");
        Check.False(OffhandRules.OffhandAllowed(MainHandKind.Caster, true), "caster: no shield (Held slot refuses it)");
        Check.True(OffhandRules.OffhandAllowed(MainHandKind.Thrown, true), "thrown: a shield may stay (ACE)");
        Check.False(OffhandRules.OffhandAllowed(MainHandKind.Thrown, false), "thrown: no off-hand weapon");

        Check.True(OffhandRules.IsShield(4, 0, false), "CombatUse Shield");
        Check.True(OffhandRules.IsShield(0, ShieldSlot, false), "a non-weapon for the Shield slot");
        Check.False(OffhandRules.IsShield(0, ShieldSlot, true), "a weapon is never a shield");
        Check.True(OffhandRules.IsTwoHanded(5, 0, 0), "CombatUse TwoHanded");
        Check.True(OffhandRules.IsTwoHanded(0, TwoHandedSlot, 0), "the TwoHanded slot");
        Check.True(OffhandRules.IsTwoHanded(0, MeleeSlot, 41), "WeaponSkill Two Handed Combat (old weapons with a MeleeWeapon slot)");
        Check.True(OffhandRules.CanBeOffhandWeapon(MeleeSlot, false), "ValidLocations exactly MeleeWeapon");
        Check.False(OffhandRules.CanBeOffhandWeapon(MeleeSlot | ShieldSlot, false), "not exactly MeleeWeapon");
        Check.False(OffhandRules.CanBeOffhandWeapon(MeleeSlot, true), "a two-hander never");
        Check.False(OffhandRules.CanBeOffhandWeapon(0, false), "ValidLocations not known: never");

        Check.Eq(OffhandRules.RuleMode(0), null, "0: the global setting");
        Check.Eq(OffhandRules.RuleMode(1), OffhandMode.Auto, "1 Auto");
        Check.Eq(OffhandRules.RuleMode(3), OffhandMode.Weapon, "3 Offhand weapon");
        Check.Eq(OffhandRules.RuleMode(unchecked((int)Shield)), null, "an item id is no mode");
        Check.True(OffhandRules.IsRuleItem(unchecked((int)Shield)), "an item id is an item");
        Check.Eq(OffhandRules.Parse("dual wield", OffhandMode.Auto), OffhandMode.Weapon, "parse 'dual wield'");
        Check.Eq(OffhandRules.Parse("None", OffhandMode.Auto), OffhandMode.None, "parse None");
        Check.Eq(OffhandRules.Parse("junk", OffhandMode.Shield), OffhandMode.Shield, "junk: the fallback");

        var shield = new OffhandCandidate(10, "Kite Shield", true, false);
        var shield2 = new OffhandCandidate(11, "Round Shield", true, true);
        var dagger = new OffhandCandidate(20, "Dagger", false, false);
        var main = new OffhandCandidate(30, "Long Sword", false, false);
        var all = new List<OffhandCandidate> { main, shield, dagger };
        OffhandPlan P(OffhandMode m, MainHandKind k, bool dw = false, bool pref = false, IReadOnlyList<OffhandCandidate>? l = null, int pick = 0)
            => OffhandPlanner.Choose(m, k, 30, l ?? all, dw, pref, true, pick, "pick");
        Check.Eq(P(OffhandMode.Auto, MainHandKind.OneHandedMelee).Id, 10, "Auto, one-hander: the shield");
        Check.Eq(P(OffhandMode.Auto, MainHandKind.TwoHanded).Id, 0, "two-hander: nothing");
        Check.Eq(P(OffhandMode.Shield, MainHandKind.Launcher).Id, 0, "launcher: nothing");
        Check.Eq(P(OffhandMode.Auto, MainHandKind.Caster).Id, 0, "caster: nothing");
        Check.Eq(P(OffhandMode.Auto, MainHandKind.Thrown).Id, 10, "thrown: a listed shield (owner, 2026-10-01)");
        Check.Eq(P(OffhandMode.Weapon, MainHandKind.Thrown, dw: true).Id, 10, "thrown: never a second weapon, the shield");
        Check.Eq(P(OffhandMode.None, MainHandKind.OneHandedMelee).Id, 0, "None: nothing");
        Check.Eq(P(OffhandMode.Auto, MainHandKind.OneHandedMelee, dw: true).Id, 10, "Auto with the skill: the shield (dual wield only when preferred)");
        Check.Eq(P(OffhandMode.Auto, MainHandKind.OneHandedMelee, dw: false).Id, 10, "Auto without the skill: the shield");
        Check.Eq(P(OffhandMode.Auto, MainHandKind.OneHandedMelee, dw: true, pref: true).Id, 20, "Auto, dual wield preferred: the dagger");
        Check.Eq(P(OffhandMode.Auto, MainHandKind.OneHandedMelee, dw: true, l: new[] { main, dagger }).Id, 0, "Auto, no shield listed, not preferred: nothing (two listed swords don't start dual wielding)");
        Check.Eq(P(OffhandMode.Weapon, MainHandKind.OneHandedMelee, dw: true).Id, 20, "Offhand weapon with the skill");
        Check.Eq(P(OffhandMode.Weapon, MainHandKind.OneHandedMelee, dw: false).Id, 10, "Offhand weapon, no skill: the shield");
        Check.Eq(P(OffhandMode.Weapon, MainHandKind.OneHandedMelee, dw: true, l: new[] { main, shield }).Id, 10, "no second weapon: the shield");
        Check.Eq(P(OffhandMode.Shield, MainHandKind.OneHandedMelee, l: new[] { main, dagger }).Id, 0, "Shield, none listed: nothing");
        Check.Eq(P(OffhandMode.Shield, MainHandKind.OneHandedMelee, l: new[] { shield, shield2 }).Id, 11, "the listed shield in the off hand first");
        Check.Eq(P(OffhandMode.Auto, MainHandKind.OneHandedMelee, l: new[] { shield, shield2 }, pick: 10).Id, 10, "an explicit pick wins");
        Check.Eq(P(OffhandMode.Auto, MainHandKind.OneHandedMelee, pick: 20).Id, 10, "an explicit weapon without the skill: the mode");
        Check.Eq(OffhandPlanner.Choose(OffhandMode.Weapon, MainHandKind.OneHandedMelee, 30, all, true, true, false).Id, 10,
            "no slot-wield call on the engine: no dual wield, the shield");
        Check.Eq(P(OffhandMode.Auto, MainHandKind.OneHandedMelee, l: new[] { main }).Id, 0, "the main weapon is never its own off hand");

        // Drakkon, 2026-10-21: Frost and Acid Hand Wraps, the off-hand pick Frost. Against a monster
        // weak to frost, Frost is the main weapon; the off hand gets Acid, not nothing (Auto with no
        // shield listed used to leave it empty).
        var frost = new OffhandCandidate(30, "Frost Hand Wraps", false, false);
        var acid = new OffhandCandidate(40, "Acid Hand Wraps", false, false);
        var wraps = new[] { frost, acid };
        OffhandPlan swapped = OffhandPlanner.Choose(OffhandMode.Auto, MainHandKind.OneHandedMelee, 30, wraps, true, false, true, 30, "Default offhand");
        Check.Eq(swapped.Id, 40, "the off-hand pick is in the main hand: the other weapon goes in the off hand");
        Check.True(swapped.Source.Contains("main hand"), "and the log says why: " + swapped.Source);
        Check.Eq(OffhandPlanner.Choose(OffhandMode.Auto, MainHandKind.OneHandedMelee, 40, wraps, true, false, true, 30, "Default offhand").Id, 30,
            "Acid in the main hand: the pick, Frost, as before");
        Check.Eq(OffhandPlanner.Choose(OffhandMode.Auto, MainHandKind.OneHandedMelee, 30, wraps, false, false, true, 30, "Default offhand").Id, 0,
            "without Dual Wield: still nothing (no shield listed)");
        Check.Eq(OffhandPlanner.Choose(OffhandMode.Auto, MainHandKind.OneHandedMelee, 30, new[] { frost, shield }, true, false, true, 30, "Default offhand").Id, 10,
            "no other weapon listed: the mode (here the shield)");
    }

    // ── The rig ───────────────────────────────────────────────────────────────

    private sealed class Rig
    {
        public readonly LegacyUiSettings S = new();
        public readonly CombatManager C;
        public readonly MonsterDamageStore Store = new();
        public readonly WorldObject Target;
        public int InHand;
        private readonly MethodInfo _equip = typeof(CombatManager).GetMethod("EquipWeaponAndSetStance", BindingFlags.Instance | BindingFlags.NonPublic)!;
        private readonly FieldInfo _lastEquip = typeof(CombatManager).GetField("_lastEquipTime", BindingFlags.Instance | BindingFlags.NonPublic)!;

        public Rig(bool dualWieldSkill = false, bool shieldOn = false, bool wieldCalls = true, bool wielderInfo = false, bool weenieErrors = false,
                   bool contents = false, bool tracker = false)
        {
            FakeHost.Reset();
            FakeHost.EquippedCalls = tracker;         // v87 GetWieldedItems (answering FakeHost.Equipped)
            FakeHost.ContentsCalls = contents;        // the client's container walk (answering FakeHost.Containers)
            FakeHost.WeaponCalls = true;
            FakeHost.SalvageCalls = wielderInfo;      // GetObjectWielderInfo (answering FakeHost.Wielded)
            FakeHost.WeenieErrorCalls = weenieErrors;
            FakeHost.WieldCalls = wieldCalls;
            FakeHost.CombatModeValue = CombatMode.Melee;
            Item(Sword, "Long Sword", 0x1, MeleeSlot, combatUse: 1);
            Item(Dagger, "Dagger", 0x1, MeleeSlot, combatUse: 1);
            Item(Great, "Greatsword", 0x1, TwoHandedSlot, combatUse: 5);
            Item(Shield, "Kite Shield", 0x2, ShieldSlot, combatUse: 4);
            Item(Round, "Round Shield", 0x2, ShieldSlot, combatUse: 4);
            Item(Bow, "Longbow", 0x100, MissileSlot, combatUse: 2);
            FakeHost.Ints[(Bow, 50)] = 1;
            Item(Arrows, "Arrow", 0x100, AmmoSlot);
            FakeHost.Ints[(Arrows, 50)] = 1; FakeHost.Ints[(Arrows, 12)] = 100; FakeHost.Ints[(Arrows, 11)] = 250;
            Item(Wand, "Wand", 0x8000, HeldSlot);
            Wield(Sword, MeleeSlot);
            if (shieldOn) Wield(Shield, ShieldSlot);
            FakeHost.Wcids[Monster] = Wcid;
            FakeHost.Skills[(Player, 49)] = dualWieldSkill ? (220, 2) : (10, 1);
            // With the container walk: a side pack in the main pack, which also turns on the
            // ownership read (GetObjectOwnershipInfo: the item's own container field).
            if (contents) { FakeHost.Names[Pack] = "Pack"; FakeHost.ItemTypes[Pack] = 0x200; FakeHost.Containers[Pack] = Player; }
            var host = FakeHost.Create(wielderInfo ? Player : 0);
            var cache = FakeHost.MakeCache(host, Player, new[] { Sword, Dagger, Great, Shield, Round, Bow, Arrows, Wand });
            C = new CombatManager(host, S, cache);
            C.SetPlayerId(Player);
            C.SetDamageStores(null, Store);
            var skills = new CharacterSkills(host);
            skills.SetPlayerId(Player);
            C.SetCharacterSkills(skills);
            C.CanCastOverride = _ => false;
            InHand = unchecked((int)Sword);
            C.InHandOverride = () => InHand;
            C.OpenPackOverride = () => unchecked((int)Pack);
            C.OffhandReadMs = 0;
            Target = new WorldObject(unchecked((int)Monster), "Test Dummy", AcObjectClass.Monster);
            List(Sword, Shield);
        }

        public void List(params uint[] ids) =>
            S.ItemRules = ids.Select(i => new ItemRule
            {
                Id = unchecked((int)i), Name = FakeHost.Names[i], Element = "", ElementSource = "",
                Action = i == Shield || i == Round ? WeaponList.ShieldAction : "Weapon",
            }).ToList();

        public void Rule(int offhandId) =>
            S.MonsterRules = new List<MonsterRule> { new() { Name = "Default" }, new() { Name = "Test Dummy", OffhandId = offhandId } };

        public static void Item(uint id, string name, uint itemType, int validSlots, int combatUse = 0)
        {
            FakeHost.Names[id] = name;
            FakeHost.ItemTypes[id] = itemType;
            FakeHost.Ints[(id, 9)] = validSlots;
            if (combatUse != 0) FakeHost.Ints[(id, 51)] = combatUse;
        }

        public static void Wield(uint id, int slot) => FakeHost.Ints[(id, 10)] = slot;
        public static void Unwield(uint id) => FakeHost.Ints.Remove((id, 10));

        /// <summary>One combat equip tick; true = ready to attack.</summary>
        public bool Tick() => (bool)_equip.Invoke(C, new object[] { Target, "Auto" })!;

        /// <summary>Skip the 2 s pacing between a cross-kind wield and the item move before it.</summary>
        public void Unpace() => _lastEquip.SetValue(C, DateTime.MinValue);

        public void Landed(uint weapon, uint old, int slot)
        {
            Unwield(old);
            Wield(weapon, slot);
            InHand = unchecked((int)weapon);
        }

        public int ShieldWields(uint id) => FakeHost.Wields.Count(w => w.Id == id && w.Mask == ShieldSlot);
    }

    // ── Combat ────────────────────────────────────────────────────────────────

    private static void OneHanderShield()
    {
        // The sword isn't in hand yet: it goes on first, no shield before it.
        var rig = new Rig();
        Rig.Unwield(Sword);
        rig.InHand = 0;
        FakeHost.CombatModeValue = CombatMode.NonCombat;
        Check.False(rig.Tick(), "not ready: the sword is being wielded");
        Check.Eq(FakeHost.Uses.LastOrDefault(), Sword, "the sword first");
        Check.Eq(FakeHost.Wields.Count, 0, "no shield before the sword is in hand");

        rig.Landed(Sword, 0, MeleeSlot);
        FakeHost.CombatModeValue = CombatMode.Melee;
        Check.False(rig.Tick(), "sword in hand: the shield wield goes out, the attack waits");
        Check.Eq(rig.ShieldWields(Shield), 1, "one wield of the listed shield into the Shield slot");
        Check.False(rig.Tick(), "in flight: wait");
        Check.Eq(FakeHost.Wields.Count, 1, "not sent again while in flight");

        Rig.Wield(Shield, ShieldSlot);
        Check.True(rig.Tick(), "sword and shield: ready");
        Check.True(rig.Tick(), "and stays ready");
        Check.Eq(FakeHost.Wields.Count, 1, "no further wields");
        Check.Eq(FakeHost.Moves.Count, 0, "nothing moved");
        Check.True(FakeHost.Logs.Any(l => l.Contains("off hand: wielding") && l.Contains("Kite Shield")), "the wield is logged");

        // An engine without the slot-wield call: the shield goes on with a plain use.
        var old = new Rig(wieldCalls: false);
        old.Tick();
        Check.Eq(FakeHost.Uses.LastOrDefault(), Shield, "no WieldItem on the engine: UseObject(shield)");
    }

    private static void TwoHanderShieldOff()
    {
        var rig = new Rig(shieldOn: true);
        rig.List(Sword, Great, Shield);
        Check.True(rig.Tick(), "sword and shield: ready");
        rig.Store.SetManualWeapon(Wcid, Great);              // the Damage tab says greatsword
        rig.List(Sword, Great, Shield);                      // a new list: the plan is worked out again

        Check.False(rig.Tick(), "tick 1: not ready");
        Check.Eq(FakeHost.Moves.Count, 1, "the shield goes into the pack first (the server refuses a two-hander with it)");
        Check.True(FakeHost.Moves.Count == 1 && FakeHost.Moves[0] == (Shield, Pack, 1), "the shield, into the open pack");
        Check.Eq(FakeHost.Uses.Count, 0, "no greatsword wield yet");
        rig.Tick();
        Check.Eq(FakeHost.Moves.Count, 1, "the move in flight: not sent again");
        Check.Eq(FakeHost.Uses.Count, 0, "nor the greatsword");

        Rig.Unwield(Shield);
        Check.False(rig.Tick(), "shield off: the greatsword wield goes out");
        Check.Eq(FakeHost.Uses.LastOrDefault(), Great, "UseObject(greatsword)");
        rig.Landed(Great, Sword, TwoHandedSlot);
        Check.True(rig.Tick(), "greatsword in hand: ready");
        Check.Eq(FakeHost.Wields.Count, 0, "no shield with a two-hander");

        // Back to the sword: the shield goes back on after it.
        rig.C.SwapTracker.MinDwellMs = 0;
        rig.Store.SetManualWeapon(Wcid, Sword);
        rig.List(Sword, Great, Shield);
        rig.Tick();
        Check.Eq(FakeHost.Uses.LastOrDefault(), Sword, "the sword");
        rig.Landed(Sword, Great, MeleeSlot);
        Check.False(rig.Tick(), "sword in hand: the shield goes back on");
        Check.Eq(rig.ShieldWields(Shield), 1, "the shield wielded again");
        Rig.Wield(Shield, ShieldSlot);
        Check.True(rig.Tick(), "ready");

        // A shield RynthAi didn't list but took off itself goes back on too.
        var rig2 = new Rig();
        Rig.Wield(Round, ShieldSlot);                        // worn by hand, not listed
        rig2.List(Sword, Great);
        Check.True(rig2.Tick(), "sword and an unlisted shield worn by hand: left alone");
        Check.Eq(FakeHost.Moves.Count, 0, "not taken off");
        rig2.Store.SetManualWeapon(Wcid, Great);
        rig2.List(Sword, Great);
        rig2.Tick();
        Check.True(FakeHost.Moves.Count == 1 && FakeHost.Moves[0].Id == Round, "the greatsword: the worn shield comes off");
        Rig.Unwield(Round);
        rig2.Tick();
        rig2.Landed(Great, Sword, TwoHandedSlot);
        rig2.Tick();
        rig2.C.SwapTracker.MinDwellMs = 0;
        rig2.Store.SetManualWeapon(Wcid, Sword);
        rig2.List(Sword, Great);
        rig2.Tick();
        rig2.Landed(Sword, Great, MeleeSlot);
        rig2.Tick();
        Check.Eq(rig2.ShieldWields(Round), 1, "what was taken off goes back on with the one-hander");
    }

    private static void BowShieldOffThenAmmo()
    {
        var rig = new Rig(shieldOn: true);
        rig.List(Sword, Bow, Shield);
        rig.Store.SetManualWeapon(Wcid, Bow);
        var plan = rig.C.PlanFor(rig.Target, null);
        Check.Eq(unchecked((uint)plan.WeaponId), Bow, "the plan: the bow (arrows in the pack)");

        Check.False(rig.Tick(), "tick 1: not ready");
        Check.True(FakeHost.Moves.Count == 1 && FakeHost.Moves[0] == (Shield, Pack, 1), "the shield goes into the pack first");
        Check.Eq(FakeHost.Uses.Count, 0, "no bow wield with the shield on (the server refuses a launcher beside it)");

        Rig.Unwield(Shield);
        rig.Unpace();
        Check.False(rig.Tick(), "shield off: the bow wield goes out");
        Check.Eq(FakeHost.Uses.LastOrDefault(), Bow, "UseObject(bow)");
        Check.True(rig.C.AmmoSwapInProgress, "the ammo step is armed");

        rig.Landed(Bow, Sword, MissileSlot);
        FakeHost.CombatModeValue = CombatMode.Missile;
        Check.False(rig.Tick(), "bow in hand: the arrows go on");
        Check.Eq(FakeHost.Uses.LastOrDefault(), Arrows, "UseObject(arrows)");
        Rig.Wield(Arrows, AmmoSlot);
        Check.True(rig.Tick(), "bow and arrows: ready to shoot");
        Check.Eq(FakeHost.Wields.Count, 0, "no shield with a launcher");
        Check.Eq(FakeHost.Moves.Count, 1, "only the shield was moved");
    }

    private static void CasterShieldOff()
    {
        // A mage with a listed wand and a shield on: ACE refuses a Held-slot caster beside a shield.
        var rig = new Rig(shieldOn: true);
        Rig.Unwield(Sword);
        rig.InHand = 0;
        rig.List(Wand, Shield);
        rig.C.CanCastOverride = _ => true;
        var gate = new WeaponSwapGate { MinIntervalMs = 3000 };
        rig.C.SetWeaponSwapGate(gate);
        FakeHost.CombatModeValue = CombatMode.NonCombat;
        Check.False(rig.Tick(), "not ready");
        Check.True(FakeHost.Moves.Count == 1 && FakeHost.Moves[0] == (Shield, Pack, 1), "the shield into the pack (under the gate the wand path holds)");
        Check.False(FakeHost.Uses.Contains(Wand), "no wand wield beside the shield");

        Rig.Unwield(Shield);
        gate.MinIntervalMs = 0;
        rig.Tick();
        Check.True(FakeHost.Uses.Contains(Wand), "the off hand empty: the wand goes on");
        Check.Eq(FakeHost.Wields.Count, 0, "no shield with a caster");
    }

    // 2026-10-06: Drakkon's axe -> wand took 4.2 s: the axe into the pack (1.2 s), then the swap
    // gate's flat 3 s before the wand. A use of the wand (the client's double-click) swaps by itself.
    private static void CasterDirectSwap()
    {
        var rig = new Rig();              // the sword in hand
        rig.List(Wand);
        rig.C.CanCastOverride = _ => true;
        Check.False(rig.Tick(), "not ready");
        Check.True(FakeHost.Uses.Contains(Wand), "UseObject(wand) with the sword still in hand");
        Check.Eq(FakeHost.Moves.Count, 0, "no pack move first");

        // The use didn't swap in 2 s (SwapTracker retries at 2.5 s): the sword goes into the pack first from now on.
        var since = typeof(CombatManager).GetField("_combatDirectSwapSince", BindingFlags.Instance | BindingFlags.NonPublic)!;
        since.SetValue(rig.C, DateTime.Now.AddSeconds(-3));
        rig.C.SwapTracker.Reset();          // its 2.5 s resolve time is up
        rig.Tick();
        Check.True(FakeHost.Moves.Any(m => m.Id == Sword), "fallback: the sword into the pack");
        Check.True(FakeHost.Logs.Any(l => l.Contains("from now on the weapon goes into a pack first")), "logged once");
    }

    private static void SwapGateSettles()
    {
        long t = 0;
        bool idle = true;
        var wielded = new Dictionary<int, bool> { [7] = true };
        var g = new WeaponSwapGate { NowMs = () => t, ServerIdle = () => idle, IsWielded = id => wielded[id] };
        Check.True(g.TryBeginSwap("shield off"), "first swap");
        g.NoteMove(7, wieldedNow: true);
        t = 500;
        Check.False(g.TryBeginSwap("weapon"), "the shield is still in hand");
        wielded[7] = false;
        t = 2999;
        Check.False(g.TryBeginSwap("weapon"), "a pack move: the full 3 s even once it reads done (the server's unwield motion, 13:58 wedge)");
        t = 3000;
        Check.True(g.TryBeginSwap("weapon"), "after 3 s");
        long claims = g.Claims;
        t = 3300;
        Check.False(g.TryBeginSwap("off hand"), "a use (the weapon): under the 400 ms floor");
        Check.Eq(g.Claims, claims, "a refused claim doesn't count");
        t = 3600;
        idle = false;
        Check.False(g.TryBeginSwap("off hand"), "the weapon's use is still unanswered");
        idle = true;
        Check.True(g.TryBeginSwap("off hand"), "answered: the off hand goes");

        // No pacing (ServerIdle unset): the flat 3 s, as before.
        t = 0;
        var flat = new WeaponSwapGate { NowMs = () => t };
        Check.True(flat.TryBeginSwap("a"), "first");
        t = 2999;
        Check.False(flat.TryBeginSwap("b"), "inside 3 s");
        t = 3000;
        Check.True(flat.TryBeginSwap("b"), "after 3 s");
    }

    private static void DualWield()
    {
        var rig = new Rig(dualWieldSkill: true);
        rig.List(Sword, Dagger, Shield);
        rig.S.PreferDualWield = true;
        Check.False(rig.Tick(), "sword in hand: the dagger goes into the off hand");
        Check.Eq(rig.ShieldWields(Dagger), 1, "WieldItem(dagger, Shield slot)");
        Check.Eq(rig.ShieldWields(Shield), 0, "not the shield");
        Rig.Wield(Dagger, ShieldSlot);
        Check.True(rig.Tick(), "sword and dagger: ready");

        // The weapon in hand is the main hand's, never the off hand's.
        rig.C.InHandOverride = null;
        Check.Eq(unchecked((uint)rig.C.InHandWeaponId(fresh: true)), Sword, "the main hand: the sword (the dagger is in the off hand)");
        rig.C.InHandOverride = () => rig.InHand;

        // The weakness / Damage tab wants the dagger, which already swings in the off hand: no swap.
        rig.Store.SetManualWeapon(Wcid, Dagger);
        rig.List(Sword, Dagger, Shield);
        int uses = FakeHost.Uses.Count, wields = FakeHost.Wields.Count;
        Check.True(rig.Tick(), "ready with both");
        Check.Eq(FakeHost.Uses.Count, uses, "no wield moving the dagger across");
        Check.Eq(FakeHost.Wields.Count, wields, "nor into the off hand again");
        Check.True(FakeHost.Logs.Any(l => l.Contains("fighting with both")), "logged");

        // A two-hander: the dagger comes off first, as a shield does.
        rig.Store.SetManualWeapon(Wcid, Great);
        rig.List(Sword, Dagger, Great, Shield);
        rig.Tick();
        Check.True(FakeHost.Moves.Any(m => m.Id == Dagger), "the off-hand dagger into the pack before the greatsword");

        // The rule asks for it without the preference too (a new rig resets the fake).
        var rig2 = new Rig(dualWieldSkill: true);
        rig2.List(Sword, Dagger, Shield);
        rig2.Rule(OffhandRules.RuleWeapon);
        rig2.Tick();
        Check.Eq(rig2.ShieldWields(Dagger), 1, "Monsters rule 'Offhand weapon': the dagger");
    }

    // 2026-10-16: Drakkon dual wielding two Hand Wraps; CurrentWieldedLocation read 0, so both
    // counted as in hand and the later listed one (the off hand's) was taken for the main weapon.
    private static void InHandFromWielderInfo()
    {
        var rig = new Rig(dualWieldSkill: true, wielderInfo: true);
        rig.List(Sword, Dagger);
        Rig.Unwield(Sword);                                   // the slot reads 0 for both
        FakeHost.Wielded[Sword] = (uint)MeleeSlot;
        FakeHost.Wielded[Dagger] = (uint)ShieldSlot;
        rig.C.InHandOverride = null;
        Check.Eq(unchecked((uint)rig.C.InHandWeaponId(fresh: true)), Sword, "the main hand: the sword, not the off-hand dagger");
        Check.Eq(unchecked((uint)rig.C.OffhandInSlot(fresh: true)), Dagger, "the off hand: the dagger");
    }

    // 2026-10-17: the off-hand Frost Hand Wraps read as the main weapon again (no slot read said
    // Shield), a swap went out for the Acid pair already in the main hand, and Drakkon fought
    // bare-handed; the off-hand wield also waited out its full confirm time every fight.
    private static void OffhandRemembered()
    {
        var rig = new Rig(dualWieldSkill: true, wielderInfo: true);
        rig.List(Sword, Dagger);
        rig.S.PreferDualWield = true;
        rig.Tick();
        Check.Eq(rig.ShieldWields(Dagger), 1, "the dagger is wielded into the off hand");
        // Both now wielded, and both read as melee-slot items (no Shield slot anywhere).
        Rig.Unwield(Sword);
        FakeHost.Wielded[Sword] = (uint)MeleeSlot;
        FakeHost.Wielded[Dagger] = (uint)MeleeSlot;
        rig.C.InHandOverride = null;
        Check.Eq(unchecked((uint)rig.C.InHandWeaponId(fresh: true)), Sword, "the main hand: the sword");
        Check.Eq(unchecked((uint)rig.C.OffhandInSlot(fresh: true)), Dagger, "the off hand: the dagger RynthAi put there");
        Check.True(FakeHost.Logs.Any(l => l.Contains("two weapons read as in hand")), "the slot reads are logged");
        Check.True(rig.Tick(), "sword and dagger: ready, no wait on the off-hand confirm");
    }

    // 2026-10-17: buffing put both Hand Wraps into the pack for the wand; their wielder records still
    // said the player, combat took him as armed, and Drakkon fought bare-handed until he died.
    private static void PackedIsNotWielded()
    {
        var rig = new Rig(wielderInfo: true, contents: true);
        rig.List(Sword, Dagger);
        Rig.Unwield(Sword);
        FakeHost.Containers[Sword] = Pack;              // in a side pack (where buffing's wand swap puts it)
        FakeHost.Wielded[Sword] = (uint)MeleeSlot;      // a wielder record left over from when it was wielded
        rig.C.InHandOverride = null;
        Check.Eq(rig.C.InHandWeaponId(fresh: true), 0, "nothing in hand: the sword is in the pack");
        FakeHost.Containers.Remove(Sword);              // wielded again
        System.Threading.Thread.Sleep(300);             // the walk refreshes after 250 ms of real time
        Check.Eq(unchecked((uint)rig.C.InHandWeaponId(fresh: true)), Sword, "back in hand: the sword");
    }

    // 2026-10-18: the player's own contents hold the wielded items too (the export listed the
    // wielded Sceptre). Read as "in the pack", buffing ignored the caster in hand and tried every
    // other one; combat could take a wielded weapon for one in the pack.
    private static void WieldedInOwnContentsIsWielded()
    {
        var rig = new Rig(wielderInfo: true, contents: true);
        rig.List(Sword);
        Rig.Unwield(Sword);
        FakeHost.Wielded[Sword] = (uint)MeleeSlot;      // wielded: the record says so
        FakeHost.ContentsExtra[Player] = new() { Sword };   // and the client lists it in the player's contents, with no container
        rig.C.InHandOverride = null;
        Check.Eq(unchecked((uint)rig.C.InHandWeaponId(fresh: true)), Sword, "in hand: the sword, though the walk lists it");
    }

    // 2026-10-18 00:07: after buffing's wand swap the equip step read the Acid Hand Wraps as wielded
    // (their wielder record still named Drakkon) while they sat in the pack, and attacked bare-handed.
    private static void EquipIgnoresStaleRecord()
    {
        var rig = new Rig(wielderInfo: true, contents: true);
        rig.List(Sword);
        Rig.Unwield(Sword);
        FakeHost.Containers[Sword] = Pack;              // in a side pack (where buffing's wand swap puts it)
        FakeHost.Wielded[Sword] = (uint)MeleeSlot;      // the stale record
        rig.C.InHandOverride = null;
        rig.Tick();
        Check.True(FakeHost.Uses.Contains(Sword), "the sword is wielded, not taken as already in hand");
    }

    // 2026-10-18: every read of the client's own fields had a way to be wrong (the slot reads 0, the
    // wielder record outlives the move into a pack, the player's contents list wielded items). With
    // the engine's tracker (the server's messages) the answer is the server's.
    private static void TrackerDecidesInHand()
    {
        var rig = new Rig(wielderInfo: true, tracker: true);
        rig.List(Sword);
        Rig.Wield(Sword, MeleeSlot);                    // the client's fields say: in hand
        FakeHost.Wielded[Sword] = (uint)MeleeSlot;
        rig.C.InHandOverride = null;
        RynthCore.Plugin.RynthAi.EquippedState.Invalidate();
        Check.Eq(rig.C.InHandWeaponId(fresh: true), 0, "the server says nothing is wielded: nothing in hand");
        rig.Tick();
        Check.True(FakeHost.Uses.Contains(Sword), "so the sword is wielded");

        FakeHost.Equipped[Sword] = (uint)MeleeSlot;     // the server's WieldObject
        RynthCore.Plugin.RynthAi.EquippedState.Invalidate();
        Check.Eq(unchecked((uint)rig.C.InHandWeaponId(fresh: true)), Sword, "now in hand");
        Rig.Unwield(Sword); FakeHost.Wielded.Remove(Sword);   // the client's fields go blank: still in hand
        RynthCore.Plugin.RynthAi.EquippedState.Invalidate();
        Check.Eq(unchecked((uint)rig.C.InHandWeaponId(fresh: true)), Sword, "the client's blank fields don't matter");
    }

    private static void TrackerHands()
    {
        var rig = new Rig(dualWieldSkill: true, wielderInfo: true, tracker: true);
        rig.List(Sword, Dagger);
        FakeHost.Equipped[Sword] = (uint)MeleeSlot;
        FakeHost.Equipped[Dagger] = (uint)ShieldSlot;
        Rig.Wield(Dagger, MeleeSlot);                   // a wrong client field: ignored
        rig.C.InHandOverride = null;
        RynthCore.Plugin.RynthAi.EquippedState.Invalidate();
        Check.Eq(unchecked((uint)rig.C.InHandWeaponId(fresh: true)), Sword, "main hand: the sword");
        Check.Eq(unchecked((uint)rig.C.OffhandInSlot(fresh: true)), Dagger, "off hand: the dagger");
    }

    // 2026-10-18 14:14: the Damage tab picked a Tetsubo (Two Handed Combat, untrained); the client refused
    // the wield unasked, and every fight took the off-hand weapon off for it and put it back.
    private static void UnmetRequirementNotChosen()
    {
        var rig = new Rig(dualWieldSkill: true);
        rig.List(Sword, Dagger, Great);
        rig.S.PreferDualWield = true;
        FakeHost.Ints[(Great, 158)] = 2;     // WieldRequirements: RawSkill
        FakeHost.Ints[(Great, 159)] = 41;    // Two Handed Combat
        FakeHost.Ints[(Great, 160)] = 250;
        FakeHost.Skills[(Player, 41)] = (0, 1);   // untrained
        Rig.Wield(Dagger, ShieldSlot);
        rig.Store.SetManualWeapon(Wcid, Great);
        for (int i = 0; i < 4; i++) { rig.Unpace(); rig.Tick(); }
        Check.False(FakeHost.Uses.Contains(Great), "the greatsword is never wielded");
        Check.False(FakeHost.Moves.Any(m => m.Id == Dagger), "the off-hand dagger never comes off for it");
        Check.False(rig.C.ListedCandidates().Any(c => c.Id == unchecked((int)Great)), "not a candidate");
        Check.True(FakeHost.Chat.Any(c => c.Contains("can't be wielded (needs Two Handed Combat, untrained)")), "the player is told: " + string.Join(" | ", FakeHost.Chat));

        // A class switch: Two Handed Combat trained (and high enough): the greatsword is a candidate again.
        FakeHost.Skills[(Player, 41)] = (300, 2);
        System.Threading.Thread.Sleep(1100);   // skill reads are cached for up to a second
        Check.True(rig.C.ListedCandidates().Any(c => c.Id == unchecked((int)Great)), "after training it: a candidate again, nothing to re-list");
    }

    // 2026-10-23: at 40% vitae, "Shashqa can't be wielded (needs Two Handed Combat 420, you have 371)",
    // though it wields fine. Its requirement is RawSkill (the base skill: no buffs, no vitae), and the
    // check read the buffed skill, which vitae lowers.
    private static void VitaeAndBaseSkillRequirement()
    {
        var rig = new Rig();
        rig.List(Sword, Great);
        FakeHost.Ints[(Great, 158)] = 2;               // WieldRequirements: RawSkill
        FakeHost.Ints[(Great, 159)] = 41;              // Two Handed Combat
        FakeHost.Ints[(Great, 160)] = 420;
        FakeHost.Skills[(Player, 41)] = (371, 2);      // buffed read, with the vitae penalty
        FakeHost.SkillLevels[(Player, 41, 1)] = 430;   // base
        var skills = new CharacterSkills(FakeHost.Create(Player));
        skills.SetPlayerId(Player);
        Check.Eq(WieldRequirements.Unmet(FakeHost.Create(Player), skills, Great), null, "base 430 meets base 420, whatever vitae does to the buffed 371");
        Check.True(rig.C.ListedCandidates().Any(c => c.Id == unchecked((int)Great)), "a candidate");
        Check.False(FakeHost.Chat.Any(c => c.Contains("can't be wielded")), "no 'can't be wielded' message: " + string.Join(" | ", FakeHost.Chat));

        FakeHost.SkillLevels[(Player, 41, 1)] = 400;
        Check.Eq(WieldRequirements.Unmet(FakeHost.Create(Player), skills, Great), "needs base Two Handed Combat 420, you have 400", "a base under the requirement is unmet");
        FakeHost.SkillLevels.Remove((Player, 41, 1));
        Check.Eq(WieldRequirements.Unmet(FakeHost.Create(Player), skills, Great), null, "the base can't be read: not known (the server's answer decides)");

        // A Skill requirement (the buffed skill) is lowered by vitae on the server too: unmet.
        FakeHost.Ints[(Great, 158)] = 1;
        Check.Eq(WieldRequirements.Unmet(FakeHost.Create(Player), skills, Great), "needs Two Handed Combat 420, you have 371", "a buffed-skill requirement reads the buffed skill");
    }

    private static void TrackerNotReadyFallsBack()
    {
        var rig = new Rig(tracker: true);
        FakeHost.EquippedNotReady = true;
        rig.List(Sword);
        rig.C.InHandOverride = null;
        RynthCore.Plugin.RynthAi.EquippedState.Invalidate();
        Check.Eq(unchecked((uint)rig.C.InHandWeaponId(fresh: true)), Sword, "-1 from the engine: the client's slot read answers (the sword reads wielded)");
    }

    // 2026-10-16: a Nodachi the character lacked the skill for was chosen whenever a monster was
    // weak to Slash: refused three times, then again two minutes later, the off hand dequipped each time.
    private static void SkillTooLowLeftOut()
    {
        var rig = new Rig(dualWieldSkill: true, weenieErrors: true);
        rig.List(Sword, Great);
        rig.Store.SetManualWeapon(Wcid, Great);
        rig.Tick();
        Check.True(FakeHost.Uses.Contains(Great), "the greatsword's wield goes out");
        FakeHost.WeenieError(CombatManager.SkillTooLowErr, 0x00A0, Great);
        rig.Unpace();
        rig.Tick();
        Check.True(rig.C.SkillTooLowFor(unchecked((int)Great)), "refused 'skill too low': left out");
        Check.True(FakeHost.Logs.Any(l => l.Contains("skill is too low to wield it")), "logged");
        Check.True(FakeHost.Chat.Any(c => c.Contains("skill is too low")), "the player is told");
        int uses = FakeHost.Uses.Count(u => u == Great);
        for (int i = 0; i < 5; i++) { rig.Unpace(); rig.Tick(); }
        Check.Eq(FakeHost.Uses.Count(u => u == Great), uses, "never sent again");
        Check.False(rig.C.ListedCandidates().Any(c => c.Id == unchecked((int)Great)), "not a candidate");
    }

    private static void NoSkillShield()
    {
        var rig = new Rig(dualWieldSkill: false);
        rig.List(Sword, Dagger, Shield);
        rig.S.PreferDualWield = true;
        rig.Rule(OffhandRules.RuleWeapon);
        Check.False(rig.Tick(), "the off hand goes on");
        Check.Eq(rig.ShieldWields(Shield), 1, "no Dual Wield skill: the shield");
        Check.Eq(rig.ShieldWields(Dagger), 0, "never the dagger");
        Check.True(FakeHost.Logs.Any(l => l.Contains("Dual Wield not trained")), "the reason is logged");

        // No shield listed either: nothing.
        var rig2 = new Rig(dualWieldSkill: false);
        rig2.List(Sword, Dagger);
        Check.True(rig2.Tick(), "ready with the sword alone");
        Check.Eq(FakeHost.Wields.Count, 0, "no off hand");
    }

    private static void NoneHandsOff()
    {
        // A shield the user wears, not listed, Offhand None everywhere.
        var rig = new Rig();
        Rig.Wield(Round, ShieldSlot);
        rig.S.OffhandDefault = "None";
        rig.List(Sword, Great, Shield);
        Check.True(rig.Tick(), "ready with the sword and the worn shield");
        Check.Eq(FakeHost.Wields.Count, 0, "the listed shield isn't swapped in");
        Check.Eq(FakeHost.Moves.Count, 0, "the worn shield stays");

        rig.Store.SetManualWeapon(Wcid, Great);
        rig.List(Sword, Great, Shield);
        for (int i = 0; i < 4; i++) rig.Tick();
        Check.Eq(FakeHost.Moves.Count, 0, "a two-hander wanted: the worn shield still stays");
        Check.Eq(FakeHost.Uses.Count, 0, "and no greatsword wield the server would refuse");
        Check.True(FakeHost.Logs.Any(l => l.Contains("Offhand is None")), "the hold is logged");

        // None from the Monsters rule, global Auto: same, and nothing goes on an empty off hand.
        var rig2 = new Rig();
        rig2.Rule(OffhandRules.RuleNone);
        Check.True(rig2.Tick(), "ready");
        Check.Eq(FakeHost.Wields.Count, 0, "rule None: the listed shield isn't wielded");

        // A wand with a None shield on: no wand wield (the server refuses it); bare-handed magic.
        var rig3 = new Rig();
        Rig.Wield(Round, ShieldSlot);
        Rig.Unwield(Sword);
        rig3.InHand = 0;
        rig3.S.OffhandDefault = "None";
        rig3.C.CanCastOverride = _ => true;
        rig3.List(Wand);
        FakeHost.CombatModeValue = CombatMode.NonCombat;
        for (int i = 0; i < 3; i++) rig3.Tick();
        Check.False(FakeHost.Uses.Contains(Wand), "no wand wield beside a None shield");
        Check.Eq(FakeHost.Moves.Count, 0, "the shield stays");
    }

    private static void UnlistedNeverUsed()
    {
        var rig = new Rig();
        rig.List(Sword);                                     // the Round Shield is only in the pack
        Check.True(rig.Tick(), "ready with the sword");
        Check.Eq(FakeHost.Wields.Count, 0, "no shield listed: none wielded");

        rig.Store.SetManualOffhand(Wcid, Round);             // the Damage tab names it
        rig.List(Sword);
        Check.True(rig.Tick(), "still ready");
        Check.Eq(FakeHost.Wields.Count, 0, "an unlisted Damage tab offhand isn't wielded");
        Check.True(FakeHost.Logs.Any(l => l.Contains("Damage tab offhand") && l.Contains("isn't in the Items list")), "logged");

        var rig2 = new Rig();
        rig2.List(Sword);
        rig2.Rule(unchecked((int)Round));                     // the rule's old item picker names it
        rig2.Tick();
        Check.Eq(FakeHost.Wields.Count, 0, "an unlisted rule offhand isn't wielded");

        // Listed shield wanted, an unlisted one worn: it comes off, the listed one goes on.
        var rig3 = new Rig();
        Rig.Wield(Round, ShieldSlot);
        rig3.Tick();
        Check.True(FakeHost.Moves.Count == 1 && FakeHost.Moves[0].Id == Round, "the worn unlisted shield into the pack");
        Check.Eq(FakeHost.Wields.Count, 0, "the listed one not before the slot is empty");
        Rig.Unwield(Round);
        rig3.Tick();
        Check.Eq(rig3.ShieldWields(Shield), 1, "then the listed shield");
        Check.Eq(rig3.ShieldWields(Round), 0, "never the unlisted one");

        // A listed Damage-tab offhand wins over Auto's first shield.
        var rig4 = new Rig();
        rig4.List(Sword, Shield, Round);
        rig4.Store.SetManualOffhand(Wcid, Round);
        rig4.Tick();
        Check.Eq(rig4.ShieldWields(Round), 1, "the Damage tab's listed offhand");
    }

    private static void NoFlipFlop()
    {
        var rig = new Rig(dualWieldSkill: true, shieldOn: true);
        rig.List(Sword, Dagger, Shield);
        rig.Rule(OffhandRules.RuleShield);
        Check.True(rig.Tick(), "sword and shield: ready");

        rig.Rule(OffhandRules.RuleWeapon);                   // this monster now wants dual wield
        rig.Tick();
        Check.True(FakeHost.Moves.Count == 1 && FakeHost.Moves[0].Id == Shield, "the shield comes off first");
        Rig.Unwield(Shield);
        rig.Tick();
        Check.Eq(rig.ShieldWields(Dagger), 1, "then the dagger goes on");
        Rig.Wield(Dagger, ShieldSlot);
        Check.True(rig.Tick(), "sword and dagger: ready");
        int moves = FakeHost.Moves.Count, wields = FakeHost.Wields.Count;

        rig.Rule(OffhandRules.RuleShield);                   // straight back to the shield
        for (int i = 0; i < 5; i++) rig.Tick();
        Check.Eq(FakeHost.Moves.Count, moves, "held: the dagger stays");
        Check.Eq(FakeHost.Wields.Count, wields, "no shield wield");
        Check.True(FakeHost.Logs.Any(l => l.Contains("off hand: keeping") && l.Contains("just swapped away from it")), "the hold is logged");

        // A shield that never lands: 3 tries, then a rest; the attack goes on.
        var rig2 = new Rig();
        rig2.C.OffhandTracker.ResolveMs = 0;
        for (int i = 0; i < 8; i++) rig2.Tick();
        Check.Eq(rig2.ShieldWields(Shield), 3, "three tries, then stop");
        Check.True(FakeHost.Logs.Any(l => l.Contains("didn't wield after 3 tries")), "the give-up is logged");
        Check.True(rig2.Tick(), "fighting on without it");
    }

    private static void ItemsAndBuffing()
    {
        var rig = new Rig();
        rig.List(Sword, Shield);
        var cands = rig.C.ListedCandidates();
        Check.Eq(cands.Count, 1, "a listed shield is no main weapon");
        Check.Eq(unchecked((uint)cands[0].Id), Sword, "only the sword");

        var rules = new List<ItemRule>
        {
            new() { Id = 1, Name = "Kite Shield", Action = WeaponList.ShieldAction, Element = "", ElementSource = "" },
            new() { Id = 2, Name = "Fire Sword", Action = "Weapon", Element = "", ElementSource = "" },
        };
        WeaponList.RefreshElements(rules, (id, name) => WeaponElements.Detect(null, null, null, name));
        Check.Eq(rules[0].Element, "", "a shield gets no element");
        Check.Eq(rules[1].Element, "Fire", "the sword does");
        var shown = WeaponList.ForDisplay(rules, r => r.Name);
        Check.Eq(shown[0].Element, "Shield", "the Items panel's element column says Shield");
        shown[0].Element = "Cold";                           // a click on the column
        var merged = WeaponList.MergeEdited(rules, shown);
        Check.Eq(merged[0].Element, "", "a shield keeps no element");
        Check.Eq(merged[0].Action, WeaponList.ShieldAction, "and stays a shield");

        // Buffing's wand: the shield blocks it (ACE refuses a caster beside a shield) unless None.
        var host = FakeHost.Create();
        var cache = FakeHost.MakeCache(host, Player, new[] { Sword, Shield, Wand });
        var s = new LegacyUiSettings();
        var bm = new BuffManager(host, s, new SpellManager(host, s), new PlayerVitalsCache());
        bm.SetWorldObjectCache(cache);
        var find = typeof(BuffManager).GetMethod("FindWieldedNonWandWeapon", BindingFlags.Instance | BindingFlags.NonPublic)!;
        Rig.Unwield(Sword);
        Rig.Wield(Shield, ShieldSlot);
        Check.Eq((int)find.Invoke(bm, new object[] { unchecked((int)Wand) })!, unchecked((int)Shield), "buffing: the shield is the blocker to put away");
        s.OffhandDefault = "None";
        Check.Eq((int)find.Invoke(bm, new object[] { unchecked((int)Wand) })!, 0, "Offhand None: buffing leaves the shield");
        s.OffhandDefault = "Auto";
        Rig.Wield(Sword, MeleeSlot);
        Check.Eq((int)find.Invoke(bm, new object[] { unchecked((int)Wand) })!, unchecked((int)Sword), "the main weapon first, as before");
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using RynthCore.Loot;
using RynthCore.Plugin.RynthAi;
using RynthCore.Plugin.RynthAi.CreatureData;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.PluginSdk;
using RynthCore.RynthAiHostTests.Fakes;
using RynthCore.RynthAiTests.Harness;

namespace RynthCore.RynthAiHostTests.Tests;

// Missile ammo (2026-10-01): swapping between launchers that shoot different ammo. The launcher's
// AmmoType (int 50) must match the ammo in the ammunition slot (0x00800000) or the server refuses
// the wield, so a bow -> crossbow swap puts the arrows away, wields the crossbow, then wields
// quarrels. A launcher with no fitting ammo isn't swapped to; listed ammo comes first; the ammo's
// element is the launcher's element; no flip-flop; thrown weapons, melee and magic untouched.
internal static class AmmoTests
{
    private const uint Player = 0x50000C01;
    private const uint Monster = 0x80001001;
    private const uint Wcid = 424242;
    private const uint Bow = 0x80000101, Crossbow = 0x80000102, Throwing = 0x80000103, Sword = 0x80000104;
    private const uint Arrows = 0x80000201, Quarrels = 0x80000202, AcidQuarrels = 0x80000203, PrismArrows = 0x80000204;
    private const uint Pack = 0x80000301;
    private const int AmmoSlot = 0x00800000, MissileSlot = 0x00400000, MeleeSlot = 0x00100000;

    public static void Register(Runner r)
    {
        r.Add("ammo: the rules (AmmoType, thrown, names, prismatic)", Rules);
        r.Add("ammo: the choice (listed first, then the weak element, then quality)", Choice);
        r.Add("ammo: bow -> crossbow puts the arrows away and wields quarrels", BowToCrossbow);
        r.Add("ammo: no fitting ammo in the pack, no swap to that launcher (logged once)", NoAmmoNoSwap);
        r.Add("ammo: listed ammo is wielded first and is never taken for a weapon", ListedPreferred);
        r.Add("ammo: no flip-flop back to the bow after a swap", NoFlipFlop);
        r.Add("ammo: a launcher's element is its ammo's (prismatic takes the launcher's)", ElementFromAmmo);
        r.Add("ammo: thrown weapons, melee and magic lists are untouched", ThrownMeleeMagicUntouched);
    }

    // ── Pure rules ────────────────────────────────────────────────────────────

    private static void Rules()
    {
        Check.Eq(MissileAmmo.LauncherAmmoType(2, "Longbow", false), 2, "AmmoType wins over the name");
        Check.Eq(MissileAmmo.LauncherAmmoType(0, "Heavy Crossbow", false), MissileAmmo.Bolt, "crossbow by name: quarrels");
        Check.Eq(MissileAmmo.LauncherAmmoType(0, "Atlatl", false), MissileAmmo.Atlatl, "atlatl by name: darts");
        Check.Eq(MissileAmmo.LauncherAmmoType(0, "Yumi", false), 0, "a launcher type not known: left alone");
        Check.Eq(MissileAmmo.LauncherAmmoType(1, "Throwing Axe", true), 0, "a thrown weapon is its own ammo");
        Check.Eq(MissileAmmo.AmmoTypeOf(0, "Deadly Acid Quarrel"), MissileAmmo.Bolt, "quarrel by name");
        Check.Eq(MissileAmmo.AmmoTypeOf(0, "Greater Atlatl Dart"), MissileAmmo.Atlatl, "dart by name");
        Check.Eq(MissileAmmo.AmmoTypeOf(0, "Arrow"), MissileAmmo.Arrow, "arrow by name");
        Check.Eq(MissileAmmo.AmmoTypeOf(0, "Bundle of Arrowheads"), 0, "a bundle is not ammo");
        Check.True(MissileAmmo.Fits(1, 1), "arrow fits a bow");
        Check.False(MissileAmmo.Fits(1, 2), "quarrels don't fit a bow");
        Check.False(MissileAmmo.Fits(1, 8), "a crystal arrow isn't an arrow (the server compares equal)");
        Check.False(MissileAmmo.Fits(0, 0), "unknown never fits");
        Check.True(MissileAmmo.IsPrismatic(0x10000000, "Arrow"), "DamageType Base: prismatic");
        Check.True(MissileAmmo.IsPrismatic(null, "Deadly Prismatic Arrow"), "named prismatic");
        var prism = new AmmoCandidate(1, "Prismatic Arrow", 1, "", true, 1, -1, false);
        Check.Eq(MissileAmmo.EffectiveElement(prism, "Fire"), "Fire", "prismatic on a fire bow: Fire");
        Check.Eq(MissileAmmo.EffectiveElement(prism, ""), "Pierce", "prismatic on a plain bow: Pierce");
        var acid = new AmmoCandidate(2, "Acid Arrow", 1, "Acid", false, 1, -1, false);
        Check.Eq(MissileAmmo.EffectiveElement(acid, "Fire"), "Acid", "elemental ammo keeps its element on a fire bow");
        Check.Eq(MissileAmmo.Label(2), "quarrels", "label");
    }

    private static void Choice()
    {
        var weak = new List<List<string>> { new() { "Acid" }, new() { "Fire" }, new() { "Pierce", "Slash" } };
        var rank = MissileAmmo.RankBy("", weak);
        var plain   = new AmmoCandidate(1, "Quarrel", 2, "", false, 1, -1, false);
        var deadly  = new AmmoCandidate(2, "Deadly Quarrel", 2, "Pierce", false, 300, -1, false);
        var acid    = new AmmoCandidate(3, "Acid Quarrel", 2, "Acid", false, 50, -1, false);
        var arrows  = new AmmoCandidate(4, "Acid Arrow", 1, "Acid", false, 400, -1, true);
        var pack = new List<AmmoCandidate> { plain, deadly, acid, arrows };
        Check.Eq(MissileAmmo.Choose(2, "", pack, rank)?.Id, 3, "weak to Acid: the acid quarrels, over better plain ones");
        Check.Eq(MissileAmmo.Choose(2, "", pack, MissileAmmo.RankBy("", null))?.Id, 2, "weakness unknown: the best quality");
        Check.Eq(MissileAmmo.Choose(2, "", pack, MissileAmmo.RankBy("Fire", weak))?.Id, 3, "rule Fire, no fire quarrels: the weakness order");
        Check.Eq(MissileAmmo.Choose(4, "", pack, rank), null, "no darts: none");
        var listedPlain = plain with { ListIndex = 3 };
        Check.Eq(MissileAmmo.Choose(2, "", new List<AmmoCandidate> { listedPlain, deadly, acid }, rank)?.Id, 1, "a listed stack beats every unlisted one");
        var prism = new AmmoCandidate(5, "Prismatic Quarrel", 2, "", true, 100, -1, false);
        Check.Eq(MissileAmmo.Choose(2, "Acid", new List<AmmoCandidate> { deadly, prism }, rank)?.Id, 5, "an acid crossbow: prismatic quarrels do Acid");
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

        public Rig(bool quarrels = true, bool acidQuarrels = false, bool prismArrows = false, bool arrowsWielded = true)
        {
            FakeHost.Reset();
            FakeHost.WeaponCalls = true;
            FakeHost.CombatModeValue = CombatMode.Missile;
            Item(Bow, "Longbow", 0x100, MissileSlot, ammoType: 1);
            Item(Crossbow, "Heavy Crossbow", 0x100, MissileSlot, ammoType: 2);
            Item(Arrows, "Arrow", 0x100, AmmoSlot, ammoType: 1, stack: 120);
            var ids = new List<uint> { Bow, Crossbow, Arrows };
            if (quarrels) { Item(Quarrels, "Quarrel", 0x100, AmmoSlot, ammoType: 2, stack: 80); ids.Add(Quarrels); }
            if (acidQuarrels) { Item(AcidQuarrels, "Deadly Acid Quarrel", 0x100, AmmoSlot, ammoType: 2, stack: 60); FakeHost.Ints[(AcidQuarrels, 45)] = 0x20; ids.Add(AcidQuarrels); }
            if (prismArrows) { Item(PrismArrows, "Deadly Prismatic Arrow", 0x100, AmmoSlot, ammoType: 1, stack: 40); FakeHost.Ints[(PrismArrows, 45)] = 0x10000000; ids.Add(PrismArrows); }
            Wield(Bow, MissileSlot);
            if (arrowsWielded) Wield(Arrows, AmmoSlot);
            FakeHost.Wcids[Monster] = Wcid;
            var host = FakeHost.Create();
            var cache = FakeHost.MakeCache(host, Player, ids);
            C = new CombatManager(host, S, cache);
            C.SetPlayerId(Player);
            C.SetDamageStores(null, Store);
            C.CanCastOverride = _ => false;
            InHand = unchecked((int)Bow);
            C.InHandOverride = () => InHand;
            C.OpenPackOverride = () => unchecked((int)Pack);
            C.AmmoOffSettleMs = 0;                            // the ticks here don't wait on the wall clock
            Target = new WorldObject(unchecked((int)Monster), "Test Dummy", AcObjectClass.Monster);
            List(Bow, Crossbow);
        }

        public void List(params uint[] ids) =>
            S.ItemRules = ids.Select(i => new ItemRule { Id = unchecked((int)i), Name = FakeHost.Names[i], Element = "", ElementSource = "" }).ToList();

        public static void Item(uint id, string name, uint itemType, int validSlots, int ammoType = 0, int stack = 0, int maxStack = 0)
        {
            FakeHost.Names[id] = name;
            FakeHost.ItemTypes[id] = itemType;
            FakeHost.Ints[(id, 9)] = validSlots;
            if (ammoType != 0) FakeHost.Ints[(id, 50)] = ammoType;
            if (stack != 0) { FakeHost.Ints[(id, 12)] = stack; FakeHost.Ints[(id, 11)] = maxStack != 0 ? maxStack : 250; }
            else if (maxStack != 0) FakeHost.Ints[(id, 11)] = maxStack;
        }

        public static void Wield(uint id, int slot) => FakeHost.Ints[(id, 10)] = slot;
        public static void Unwield(uint id) => FakeHost.Ints.Remove((id, 10));

        /// <summary>One combat equip tick; true = ready to attack.</summary>
        public bool Tick() => (bool)_equip.Invoke(C, new object[] { Target, "Auto" })!;

        /// <summary>The weapon a swap landed on: in hand, wielded, the old one off.</summary>
        public void Landed(uint weapon, uint old)
        {
            Unwield(old);
            Wield(weapon, MissileSlot);
            InHand = unchecked((int)weapon);
        }
    }

    // ── Combat ────────────────────────────────────────────────────────────────

    private static void BowToCrossbow()
    {
        var rig = new Rig();
        rig.Store.SetManualWeapon(Wcid, Crossbow);           // the Damage tab says crossbow for this monster
        var plan = rig.C.PlanFor(rig.Target, null);
        Check.Eq(unchecked((uint)plan.WeaponId), Crossbow, "the plan: the crossbow (quarrels in the pack)");

        Check.False(rig.Tick(), "tick 1: not ready");
        Check.Eq(FakeHost.Moves.Count, 1, "tick 1: the arrows go into the pack first (the server refuses a crossbow with arrows on)");
        Check.True(FakeHost.Moves.Count == 1 && FakeHost.Moves[0] == (Arrows, Pack, 120), "the whole arrow stack, into the open pack");
        Check.Eq(FakeHost.Uses.Count, 0, "no wield yet");
        Check.True(rig.C.AmmoSwapInProgress, "ammo crafting holds off");

        rig.Tick();
        Check.Eq(FakeHost.Moves.Count, 1, "the move in flight: not sent again");
        Check.Eq(FakeHost.Uses.Count, 0, "nor the crossbow before the arrows are off");

        Rig.Unwield(Arrows);                                  // the arrows are in the pack
        rig.C.AmmoOffSettleMs = 60_000;
        Check.False(rig.Tick(), "arrows just read off: the settle");
        Check.Eq(FakeHost.Uses.Count, 0, "no crossbow inside the settle");
        rig.C.AmmoOffSettleMs = 0;
        Check.False(rig.Tick(), "settled: the crossbow wield is sent");
        Check.Eq(FakeHost.Uses.Count, 1, "one UseObject");
        Check.Eq(FakeHost.Uses.LastOrDefault(), Crossbow, "for the crossbow");

        rig.Tick();
        Check.Eq(FakeHost.Uses.Count, 1, "one swap in flight: nothing more");

        rig.Landed(Crossbow, Bow);
        Check.False(rig.Tick(), "crossbow in hand: wielding quarrels, not ready yet");
        Check.Eq(FakeHost.Uses.Count, 2, "a second UseObject");
        Check.Eq(FakeHost.Uses.LastOrDefault(), Quarrels, "for the quarrels");
        Check.False(rig.Tick(), "the quarrels in flight: wait");
        Check.Eq(FakeHost.Uses.Count, 2, "not sent again while in flight");

        Rig.Wield(Quarrels, AmmoSlot);                        // the quarrels landed
        Check.True(rig.Tick(), "crossbow and quarrels: ready to shoot");
        Check.False(rig.C.AmmoSwapInProgress, "crafting may run again");
        Check.Eq(FakeHost.Uses.Count, 2, "no further item actions");
        Check.True(FakeHost.Logs.Any(l => l.Contains("ammo in:")), "the ammo landing is logged");

        // Quarrels that never arrive: 3 tries, then left to ammo crafting.
        var rig2 = new Rig(arrowsWielded: false);
        rig2.Store.SetManualWeapon(Wcid, Crossbow);
        rig2.Tick();                                          // nothing to put away: the crossbow wield goes out
        Check.Eq(FakeHost.Uses.LastOrDefault(), Crossbow, "no arrows on: straight to the crossbow");
        rig2.Landed(Crossbow, Bow);
        var tracker = rig2.C.AmmoTracker;
        tracker.ResolveMs = 0;                                // each try times out at once
        for (int i = 0; i < 6; i++) rig2.Tick();
        Check.Eq(FakeHost.Uses.Count(u => u == Quarrels), 3, "three tries for the quarrels, then stop");
        Check.True(FakeHost.Logs.Any(l => l.Contains("didn't wield after 3 tries")), "the give-up is logged");
        Check.False(rig2.C.AmmoSwapInProgress, "crafting takes over");
    }

    private static void NoAmmoNoSwap()
    {
        var rig = new Rig(quarrels: false);
        rig.Store.SetManualWeapon(Wcid, Crossbow);
        var plan = rig.C.PlanFor(rig.Target, null);
        Check.Eq(unchecked((uint)plan.WeaponId), Bow, "no quarrels: the bow stays");
        Check.True(rig.Tick(), "ready with the bow and its arrows");
        Check.Eq(FakeHost.Uses.Count, 0, "no wield");
        Check.Eq(FakeHost.Moves.Count, 0, "the arrows stay on");

        FakeHost.Logs.Clear();
        rig.C.ApplyAmmo(rig.C.ListedCandidates(), "", null);
        rig.C.ApplyAmmo(rig.C.ListedCandidates(), "", null);
        Check.Eq(FakeHost.Logs.Count(l => l.Contains("has no quarrels in the pack")), 0, "already said once");
        var fresh = new Rig(quarrels: false);
        fresh.C.ApplyAmmo(fresh.C.ListedCandidates(), "", null);
        fresh.C.ApplyAmmo(fresh.C.ListedCandidates(), "", null);
        Check.Eq(FakeHost.Logs.Count(l => l.Contains("'Heavy Crossbow' has no quarrels in the pack")), 1, "logged once");

        // No launcher has ammo at all (none in hand): nothing changes, the first launcher stays the plan.
        var dry = new Rig(quarrels: false, arrowsWielded: false);
        Rig.Item(Arrows, "Arrow", 0x100, AmmoSlot, ammoType: 8, stack: 120);   // crystal arrows: fit neither
        dry.InHand = 0;
        var list = dry.C.ListedCandidates();
        var applied = dry.C.ApplyAmmo(list, "", null);
        Check.Eq(applied.Count, 2, "both launchers kept when none has ammo (crafting makes some for the first)");
    }

    private static void ListedPreferred()
    {
        var rig = new Rig(acidQuarrels: true, arrowsWielded: false);
        rig.Store.SetManualWeapon(Wcid, Crossbow);
        rig.List(Bow, Crossbow, Quarrels);                    // plain quarrels listed; Deadly Acid ones only in the pack
        var cands = rig.C.ListedCandidates();
        Check.False(cands.Any(c => c.Id == unchecked((int)Quarrels)), "listed quarrels are not a weapon to wield");
        Check.Eq(cands.Count, 2, "bow and crossbow");

        rig.Tick();
        Check.Eq(FakeHost.Uses.LastOrDefault(), Crossbow, "the crossbow first");
        rig.Landed(Crossbow, Bow);
        rig.Tick();
        Check.Eq(FakeHost.Uses.LastOrDefault(), Quarrels, "the listed quarrels, over the better unlisted Deadly Acid ones");

        // Unlisted: the better stack.
        var rig2 = new Rig(acidQuarrels: true, arrowsWielded: false);
        rig2.Store.SetManualWeapon(Wcid, Crossbow);
        rig2.Tick();
        rig2.Landed(Crossbow, Bow);
        rig2.Tick();
        Check.Eq(FakeHost.Uses.LastOrDefault(), AcidQuarrels, "nothing listed: the best quarrels in the pack");
    }

    private static void NoFlipFlop()
    {
        var rig = new Rig();
        rig.Store.SetManualWeapon(Wcid, Crossbow);
        rig.Tick();                                           // arrows off
        Rig.Unwield(Arrows);
        rig.Tick();                                           // crossbow wield
        rig.Landed(Crossbow, Bow);
        rig.Tick();                                           // quarrels wield
        Rig.Wield(Quarrels, AmmoSlot);
        Check.True(rig.Tick(), "crossbow and quarrels in");
        int uses = FakeHost.Uses.Count, moves = FakeHost.Moves.Count;

        rig.Store.SetManualWeapon(Wcid, Bow);                 // now the Damage tab wants the bow back
        rig.List(Bow, Crossbow);                              // a new list: the plan is worked out again
        var plan = rig.C.PlanFor(rig.Target, null);
        Check.Eq(unchecked((uint)plan.WeaponId), Bow, "the plan says bow (arrows in the pack)");
        for (int i = 0; i < 5; i++) rig.Tick();
        Check.Eq(FakeHost.Uses.Count, uses, "straight back to the bow: held, no wield");
        Check.Eq(FakeHost.Moves.Count, moves, "and the quarrels stay on");
        Check.True(FakeHost.Logs.Any(l => l.Contains("just swapped away from it")), "the hold is logged");
    }

    private static void ElementFromAmmo()
    {
        var rig = new Rig(acidQuarrels: true);
        var acidRule = new MonsterRule { Name = "Test Dummy", DamageType = "Acid" };
        var plan = rig.C.PlanFor(rig.Target, acidRule);
        Check.Eq(unchecked((uint)plan.WeaponId), Crossbow, "rule Acid: the crossbow, whose Deadly Acid quarrels do Acid");
        Check.Eq(plan.Element, "Acid", "element Acid");

        // An elemental launcher shoots its own element only with prismatic ammo.
        var rig2 = new Rig(prismArrows: true, quarrels: false, arrowsWielded: false);
        FakeHost.Ints[(Bow, 18)] = 0x20;                      // a fire bow (icon glow)
        rig2.InHand = unchecked((int)Crossbow);               // crossbow in hand, no quarrels left
        Rig.Unwield(Bow); Rig.Wield(Crossbow, MissileSlot);
        var cands = rig2.C.ApplyAmmo(rig2.C.ListedCandidates(), "", null);
        var bow = cands.FirstOrDefault(c => c.Id == unchecked((int)Bow));
        Check.Eq(bow?.Element, "Fire", "a fire bow with prismatic arrows does Fire");
        FakeHost.Ints[(PrismArrows, 50)] = 8;                 // the prismatic stack gone (crystal type: fits nothing)
        FakeHost.Ints[(Arrows, 45)] = 0x2;                    // only plain, identified Pierce arrows left
        cands = rig2.C.ApplyAmmo(rig2.C.ListedCandidates(), "", null);
        bow = cands.FirstOrDefault(c => c.Id == unchecked((int)Bow));
        Check.Eq(bow?.Element, "Pierce", "the same fire bow with plain arrows does Pierce (the arrows' element)");

        // An element picked by hand in the Items panel wins over the ammo's (owner's call, 2026-10-01).
        var bowRule = rig2.S.ItemRules.First(r => r.Id == unchecked((int)Bow));
        bowRule.Element = "Fire";
        bowRule.ElementSource = "set";
        cands = rig2.C.ApplyAmmo(rig2.C.ListedCandidates(), "", null);
        bow = cands.FirstOrDefault(c => c.Id == unchecked((int)Bow));
        Check.Eq(bow?.Element, "Fire", "hand-set Fire on the bow stays Fire with plain arrows");
    }

    private static void ThrownMeleeMagicUntouched()
    {
        var rig = new Rig(arrowsWielded: false);
        Rig.Item(Throwing, "Throwing Axe", 0x100, MissileSlot, stack: 30, maxStack: 50);
        Rig.Item(Sword, "Long Sword", 0x1, MeleeSlot);
        var host = FakeHost.Create();
        var cache = FakeHost.MakeCache(host, Player, new[] { Bow, Crossbow, Arrows, Quarrels, Throwing, Sword });
        var c = new CombatManager(host, rig.S, cache);
        c.SetPlayerId(Player);
        c.CanCastOverride = _ => false;
        c.InHandOverride = () => unchecked((int)Throwing);
        c.OpenPackOverride = () => unchecked((int)Pack);

        rig.List(Throwing);
        var list = c.ListedCandidates();
        var applied = c.ApplyAmmo(list, "", null);
        Check.Eq(applied.Count, 1, "the thrown weapon stays a candidate (no ammo needed)");
        Check.Eq(applied.FirstOrDefault()?.Element, list.FirstOrDefault()?.Element, "its element unchanged");

        rig.List(Sword);
        list = c.ListedCandidates();
        Check.True(ReferenceEquals(c.ApplyAmmo(list, "", null), list), "a melee list comes back as it is");

        // A thrown-weapon character still counts its throwing weapons as ammo (unchanged).
        Rig.Wield(Throwing, MissileSlot);
        var has = typeof(CombatManager).GetMethod("HasWieldedAmmo", BindingFlags.Instance | BindingFlags.NonPublic)!;
        Check.True((bool)has.Invoke(c, null)!, "a wielded thrown weapon is its own ammo");
        Check.Eq(FakeHost.Moves.Count, 0, "nothing moved");
    }
}

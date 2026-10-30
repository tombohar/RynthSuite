using System;
using System.Linq;
using System.Reflection;
using RynthCore.Loot;
using RynthCore.Plugin.RynthAi;
using RynthCore.Plugin.RynthAi.CreatureData;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.RynthAiHostTests.Fakes;
using RynthCore.RynthAiTests.Harness;

namespace RynthCore.RynthAiHostTests.Tests;

// Mixed launchers (2026-10-12): a crossbow with quarrels to a bow with arrows. The log of
// 2026-10-11 14:45:11-17 took 6.4 s and flickered the stance Missile/peace/Melee: combat asked
// for Missile mode while the bow (or its arrows) wasn't on, and each step waited the swap gate's
// full 3 s. Now no stance request goes out until the launcher and its ammo are on, and the
// launcher goes 400 ms after the quarrels read off.
internal static class LauncherSwapTests
{
    private const uint Player = 0x50000C01;
    private const uint Monster = 0x80001001;
    private const uint Wcid = 434344;
    private const uint Crossbow = 0x80000601, Bow = 0x80000602, Quarrels = 0x80000603, Arrows = 0x80000604;
    private const uint Pack = 0x80000501;
    private const int MissileSlot = 0x00400000, AmmoSlot = 0x00800000;
    private const int PropAmmoType = 50, PropStack = 12, PropMaxStack = 11;

    public static void Register(Runner r)
    {
        r.Add("launcher swap: no stance request until the launcher and its ammo are on", NoStanceMidSwap);
        r.Add("launcher swap: the launcher goes once the old ammo reads off (not 3 s + 2 s)", ShortWaitAfterAmmoOff);
        r.Add("launcher swap: from peace mode the dequip still holds the stance", PeaceStartHoldsStance);
        r.Add("swap gate: MoveDone lets the next swap go on the server's answers", GateMoveDone);
        r.Add("swap holds: a nearly dead target keeps the crossbow (nothing of the swap started)", LowHealthKeepsLauncher);
        r.Add("swap holds: a swap under way finishes even when the target gets low (quarrels already off)", LowHealthMidSwapFinishes);
        r.Add("swap holds: a lock after a kill swaps at once; a switch from a live target waits", SettleOnlyAfterSwitch);
    }

    /// <summary>
    /// WeaponSwapSettleMs (2026-10-15): Kelpie's logs had 22 of 25 settle holds swap anyway within
    /// 3 s, nearly all on a fresh lock after a kill. Only a switch from another live target waits.
    /// </summary>
    private static void SettleOnlyAfterSwitch()
    {
        var rig = new Rig(CombatMode.Missile);
        rig.Lock(Monster, wasSwitch: false);
        Check.False(rig.Tick(0), "fresh lock 0 ms ago: the swap starts");
        Check.True(FakeHost.Moves.Count == 1 && FakeHost.Moves[0].Id == Quarrels, "the quarrels go into the pack at once");
        Check.False(FakeHost.Logs.Any(l => l.Contains("not swapping")), "no settle hold logged");

        rig = new Rig(CombatMode.Missile);
        rig.Lock(Monster, wasSwitch: true);
        Check.False(rig.Tick(0), "switched 0 ms ago: the attack waits for the target to settle");
        Check.Eq(FakeHost.Moves.Count, 0, "nothing of the swap started");
        Check.Eq(FakeHost.Uses.Count, 0, "no bow wield");
        Check.True(FakeHost.Logs.Any(l => l.Contains("not swapping") && l.Contains("target switched")), "the hold says why");

        rig.S.WeaponSwapSettleMs = 0;
        rig.Tick(100);
        Check.True(FakeHost.Moves.Count == 1 && FakeHost.Moves[0].Id == Quarrels, "setting 0: the swap starts");
    }

    /// <summary>WeaponSwapMinHealthPct: no swap starts onto a target at 25% or less.</summary>
    private static void LowHealthKeepsLauncher()
    {
        var rig = new Rig(CombatMode.Missile);
        rig.Cache.OnUpdateHealth(Monster, 0.2f);
        Check.True(rig.Tick(0), "target at 20%: fight on with the crossbow");
        Check.Eq(FakeHost.Moves.Count, 0, "the quarrels stay on");
        Check.Eq(FakeHost.Uses.Count, 0, "no bow wield");
        Check.True(FakeHost.Logs.Any(l => l.Contains("not swapping") && l.Contains("20% health")), "the hold is logged");

        rig.S.WeaponSwapMinHealthPct = 0;
        rig.Tick(100);
        Check.True(FakeHost.Moves.Count == 1 && FakeHost.Moves[0].Id == Quarrels, "setting off: the swap starts");
    }

    /// <summary>
    /// The ammo fix (2026-10-12) must hold: once the quarrels went into the pack, a hold rule may
    /// not keep the crossbow (it would stand there with no ammo on); the bow goes on as planned.
    /// </summary>
    private static void LowHealthMidSwapFinishes()
    {
        var rig = new Rig(CombatMode.Missile);
        rig.C.AmmoOffSettleMs = 0;
        Check.False(rig.Tick(0), "full health: the swap starts");
        Check.True(FakeHost.Moves.Count == 1 && FakeHost.Moves[0].Id == Quarrels, "the quarrels go into the pack");

        rig.Cache.OnUpdateHealth(Monster, 0.1f);
        Rig.Unwield(Quarrels);
        FakeHost.CombatModeValue = CombatMode.NonCombat;
        Check.False(rig.Tick(500), "quarrels off, target at 10%: still not ready");
        Check.Eq(FakeHost.Uses.LastOrDefault(), Bow, "the bow goes on anyway");
        Check.Eq(FakeHost.CombatModeRequests.Count, 0, "no Missile request until the bow and arrows are on");

        rig.Landed(Bow, Crossbow, MissileSlot);
        rig.Tick(1000);
        Check.Eq(FakeHost.Uses.LastOrDefault(), Arrows, "then its arrows");
    }

    // â”€â”€ The rig â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    private sealed class Rig
    {
        public readonly LegacyUiSettings S = new();
        public readonly CombatManager C;
        public readonly MonsterDamageStore Store = new();
        public readonly WorldObject Target;
        public readonly WeaponSwapGate Gate;
        public readonly WorldObjectCache Cache;
        public long T;
        public int InHand;
        private readonly MethodInfo _equip = typeof(CombatManager).GetMethod("EquipWeaponAndSetStance", BindingFlags.Instance | BindingFlags.NonPublic)!;

        public Rig(int startMode)
        {
            FakeHost.Reset();
            FakeHost.WeaponCalls = true;
            FakeHost.CombatModeCalls = true;
            FakeHost.CombatModeValue = startMode;
            Item(Crossbow, "Heavy Crossbow", MissileSlot, ammoType: 2);
            Item(Bow, "Longbow", MissileSlot, ammoType: 1);
            Item(Quarrels, "Quarrel", AmmoSlot, ammoType: 2, stack: true);
            Item(Arrows, "Arrow", AmmoSlot, ammoType: 1, stack: true);
            Wield(Crossbow, MissileSlot);
            Wield(Quarrels, AmmoSlot);
            FakeHost.Wcids[Monster] = Wcid;
            var host = FakeHost.Create();
            var cache = FakeHost.MakeCache(host, Player, new[] { Crossbow, Bow, Quarrels, Arrows });
            Cache = cache;
            C = new CombatManager(host, S, cache);
            C.SetPlayerId(Player);
            C.SetDamageStores(null, Store);
            var skills = new CharacterSkills(host);
            skills.SetPlayerId(Player);
            C.SetCharacterSkills(skills);
            C.CanCastOverride = _ => false;
            InHand = unchecked((int)Crossbow);
            C.InHandOverride = () => InHand;
            C.OpenPackOverride = () => unchecked((int)Pack);
            C.OffhandReadMs = 0;
            C.SwapTracker.MinDwellMs = 0;
            // The live gate's settings, on a test clock; the server answers at once.
            Gate = new WeaponSwapGate { MinIntervalMs = 3000, NowMs = () => T, ServerIdle = () => true, IsWielded = _ => false };
            C.SetWeaponSwapGate(Gate);
            Target = new WorldObject(unchecked((int)Monster), "Test Dummy", AcObjectClass.Monster);
            S.ItemRules = new[] { Crossbow, Bow }.Select(i => new ItemRule
            {
                Id = unchecked((int)i), Name = FakeHost.Names[i], Element = "", ElementSource = "", Action = "Weapon",
            }).ToList();
            Store.SetManualWeapon(Wcid, Bow);                // the Damage tab says the bow
        }

        private static void Item(uint id, string name, int validSlots, int ammoType, bool stack = false)
        {
            FakeHost.Names[id] = name;
            FakeHost.ItemTypes[id] = 0x100;                  // MissileWeapon
            FakeHost.Ints[(id, 9)] = validSlots;
            FakeHost.Ints[(id, PropAmmoType)] = ammoType;
            if (stack) { FakeHost.Ints[(id, PropStack)] = 100; FakeHost.Ints[(id, PropMaxStack)] = 250; }
        }

        public static void Wield(uint id, int slot) => FakeHost.Ints[(id, 10)] = slot;
        public static void Unwield(uint id) => FakeHost.Ints.Remove((id, 10));

        /// <summary>One combat equip tick at <paramref name="atMs"/> on the gate's clock; true = ready to attack.</summary>
        public bool Tick(long atMs)
        {
            T = atMs;
            return (bool)_equip.Invoke(C, new object[] { Target, "Auto" })!;
        }

        /// <summary>Combat locked <paramref name="target"/> just now (a switch from another live target or not).</summary>
        public void Lock(uint target, bool wasSwitch)
        {
            const BindingFlags F = BindingFlags.Instance | BindingFlags.NonPublic;
            typeof(CombatManager).GetField("_lockedTargetId", F)!.SetValue(C, unchecked((int)target));
            typeof(CombatManager).GetField("_targetLockedAt", F)!.SetValue(C, RynthClock.Now);
            typeof(CombatManager).GetField("_lockWasSwitch", F)!.SetValue(C, wasSwitch);
        }

        public void Landed(uint weapon, uint old, int slot)
        {
            Unwield(old);
            Wield(weapon, slot);
            InHand = unchecked((int)weapon);
        }
    }

    // â”€â”€ Tests â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    private static void NoStanceMidSwap()
    {
        var rig = new Rig(CombatMode.Missile);
        rig.C.AmmoOffSettleMs = 0;

        Check.False(rig.Tick(0), "tick 1: not ready");
        Check.True(FakeHost.Moves.Count == 1 && FakeHost.Moves[0].Id == Quarrels, "the quarrels go into the pack first");
        Check.Eq(FakeHost.Uses.Count, 0, "no bow wield with the quarrels on");

        // The server takes the quarrels and drops the character to peace mode.
        Rig.Unwield(Quarrels);
        FakeHost.CombatModeValue = CombatMode.NonCombat;
        Check.False(rig.Tick(500), "quarrels off: the bow wield goes out");
        Check.Eq(FakeHost.Uses.LastOrDefault(), Bow, "UseObject(bow)");
        Check.Eq(FakeHost.CombatModeRequests.Count, 0, "no Missile request with the bow not on (was the flicker)");
        for (int i = 1; i <= 3; i++) rig.Tick(500 + i * 100);
        Check.Eq(FakeHost.CombatModeRequests.Count, 0, "none while the bow's wield is in flight");

        rig.Landed(Bow, Crossbow, MissileSlot);
        Check.False(rig.Tick(1000), "bow in hand: the arrows go on");
        Check.Eq(FakeHost.Uses.LastOrDefault(), Arrows, "UseObject(arrows)");
        Check.Eq(FakeHost.CombatModeRequests.Count, 0, "no Missile request before the arrows are on");

        Rig.Wield(Arrows, AmmoSlot);
        Check.False(rig.Tick(1500), "bow and arrows: the stance goes out");
        Check.True(FakeHost.CombatModeRequests.SequenceEqual(new[] { CombatMode.Missile }), "exactly one Missile request, after the arrows");
        FakeHost.CombatModeValue = CombatMode.Missile;
        Check.True(rig.Tick(2500), "in Missile mode: ready to shoot");
    }

    private static void ShortWaitAfterAmmoOff()
    {
        var rig = new Rig(CombatMode.Missile);
        rig.Tick(0);
        Check.True(FakeHost.Moves.Count == 1 && FakeHost.Moves[0].Id == Quarrels, "the quarrels go into the pack");

        Rig.Unwield(Quarrels);
        FakeHost.CombatModeValue = CombatMode.NonCombat;
        rig.Tick(150);
        Check.Eq(FakeHost.Uses.Count, 0, "just read off: the 400 ms settle");

        // The settle passed (the test can't wait on the wall clock): the launcher goes far inside
        // the gate's 3 s and the old 2 s equip pacing.
        rig.C.AmmoOffSettleMs = 0;
        rig.Tick(600);
        Check.Eq(FakeHost.Uses.LastOrDefault(), Bow, "the bow at 0.6 s (was 3.0 s)");

        rig.Landed(Bow, Crossbow, MissileSlot);
        rig.Tick(1100);
        Check.Eq(FakeHost.Uses.LastOrDefault(), Arrows, "the arrows at 1.1 s (was 6.0 s)");
        Check.Eq(FakeHost.Moves.Count, 1, "one pack move in all");
    }

    private static void PeaceStartHoldsStance()
    {
        // Peace mode with the crossbow and quarrels on: the not-wielded branch dequips; its stance
        // request (sent with any wield before) is held for the whole swap.
        var rig = new Rig(CombatMode.NonCombat);
        rig.C.AmmoOffSettleMs = 0;
        Check.False(rig.Tick(0), "not ready");
        Check.True(FakeHost.Moves.Count == 1 && FakeHost.Moves[0].Id == Quarrels, "the quarrels go into the pack");
        Check.Eq(FakeHost.CombatModeRequests.Count, 0, "no Missile request with the wrong ammo on");
        Check.True(FakeHost.Logs.Any(l => l.Contains("stance held")), "the hold is logged");

        Rig.Unwield(Quarrels);
        rig.Tick(500);
        Check.Eq(FakeHost.Uses.LastOrDefault(), Bow, "then the bow");
        Check.Eq(FakeHost.CombatModeRequests.Count, 0, "still no Missile request");
    }

    private static void GateMoveDone()
    {
        long t = 0;
        var g = new WeaponSwapGate { NowMs = () => t, ServerIdle = () => true, IsWielded = _ => false };
        Check.True(g.TryBeginSwap("ammo off"), "first swap");
        g.NoteMove(7, wieldedNow: true);
        t = 500;
        Check.False(g.TryBeginSwap("launcher"), "a noted move: held");
        g.MoveDone(8);
        Check.False(g.TryBeginSwap("launcher"), "another item's MoveDone changes nothing");
        g.MoveDone(7);
        Check.True(g.TryBeginSwap("launcher"), "MoveDone: the launcher goes at 0.5 s");
        t = 700;
        Check.False(g.TryBeginSwap("ammo"), "under the 400 ms floor");
    }
}

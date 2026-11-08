using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using RynthCore.Loot;
using RynthCore.Plugin.RynthAi;
using RynthCore.Plugin.RynthAi.CreatureData;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.Plugin.Shared;
using RynthCore.RynthAiHostTests.Fakes;
using RynthCore.RynthAiTests.Harness;

namespace RynthCore.RynthAiHostTests.Tests;

// The owner's report, 2026-11-07: "if you swap from bow to crossbow, it unwields your bow and your
// infinite arrows and wields your crossbow, but not your infinite quarrels. So you dry fire."
// Servers make their own infinite ammo: a single item that doesn't stack, UnlimitedUse (known once
// appraised), names with no arrow/quarrel in them, sometimes no AmmoType at all. Combat wielded the
// new ammo only right after its own launcher swap (30 s, and only when a fitting stack was found when
// the swap began); any other empty quiver was left to ammo crafting, which knows ammo by name only.
//
// The fights here run against a small simulated ACE (Player_Inventory.CheckWeaponCollision): a
// launcher is refused while ammo of another type (or of none) is on, ammo is refused when both it
// and the launcher in hand have a type and they differ, taking ammo off in Missile mode drops to
// peace, and Missile mode is refused with a launcher and nothing it can shoot.
internal static class MissileAmmoSwapTests
{
    private const uint Player = 0x50000F01;
    private const uint Monster = 0x80001F01;
    private const uint Wcid = 515151;
    private const uint Bow = 0x80000F01, Crossbow = 0x80000F02, Axe = 0x80000F03, Wand = 0x80000F04;
    private const uint InfArrows = 0x80000F11, InfQuarrels = 0x80000F12, DeadlyQuarrels = 0x80000F13, Arrows = 0x80000F14;
    private const int MeleeSlot = 0x00100000, ShieldSlot = 0x00200000, MissileSlot = 0x00400000, AmmoSlot = 0x00800000,
                      HeldSlot = 0x01000000, TwoHandedSlot = 0x02000000;
    private const int MainSlots = MeleeSlot | MissileSlot | HeldSlot | TwoHandedSlot;
    private static int I(uint id) => unchecked((int)id);

    public static void Register(Runner r)
    {
        r.Add("missile ammo (owner 2026-11-07): the rules - infinite, ammo of no type, the pick order", Rules);
        r.Add("missile ammo: the owner's report - bow + infinite arrows -> crossbow: arrows off, crossbow on, infinite quarrels on, then Missile", OwnersReport);
        r.Add("missile ammo: infinite ammo of no AmmoType and an odd name: off before the crossbow, the crossbow's own goes on (not the bow's)", UntypedInfinite);
        r.Add("missile ammo: UnlimitedUse on a stack (read after the appraisal) beats a better finite stack", UnlimitedUseWins);
        r.Add("missile ammo: a crossbow in hand with an empty quiver that combat didn't swap gets its quarrels (was left to crafting)", EmptyQuiverNotOurSwap);
        r.Add("missile ammo: the ammo still goes on when the crossbow lands after the 30 s swap window", AfterTheWindow);
        r.Add("missile ammo: arrows on with a crossbow in hand - no shot; the arrows off, then quarrels", WrongAmmoInHand);
        r.Add("missile ammo: no ammo the launcher can shoot - one chat line, no wield, no shot; quarrels found later go on", NoAmmoSaysOnce);
        r.Add("missile ammo: bow -> thrown weapon puts the arrows away first and wields no ammo for it; back to the bow, the arrows go on", ThrownWeapon);
        r.Add("missile ammo: Fast Weapon Swap from the wand (a monster while healing): arrows off, the crossbow, then quarrels, then Missile", FastSwapFromWand);
        r.Add("missile ammo: weapons back after heals puts the bow back with its arrows in the pack: they go on when the fight starts (Fast Weapon Swap on and off)", WeaponsBackAfterHeals);
        r.Add("missile ammo: a launcher named for bolts is not its own ammo (no dry fire)", LauncherNotItsOwnAmmo);
        r.Add("missile crafting: a crossbow not named so, with its odd-named quarrels on, is left alone (no arrows at it)", CraftingKnowsTypes);
        r.Add("missile crafting: odd-named quarrels in the pack are wielded for that crossbow", CraftingWieldsOddNamed);
    }

    // ── Pure rules ────────────────────────────────────────────────────────────

    private static void Rules()
    {
        Check.True(MissileAmmo.LooksInfinite(true, 50, 250, "Arrow"), "UnlimitedUse: infinite, stack or not");
        Check.True(MissileAmmo.LooksInfinite(false, 0, 0, "Stormcaller's Sheaf"), "a single item with no stack: infinite");
        Check.True(MissileAmmo.LooksInfinite(false, 1, 1, "Sheaf"), "a stack of 1 that can't grow: infinite");
        Check.False(MissileAmmo.LooksInfinite(false, 1, 250, "Arrow"), "the last arrow of a stack is not infinite");
        Check.True(MissileAmmo.LooksInfinite(false, 40, 250, "Infinite Deadly Prismatic Arrow"), "named so");

        Check.True(MissileAmmo.CanShoot(2, 2), "quarrels in a crossbow");
        Check.True(MissileAmmo.CanShoot(2, 0), "ammo of no type: the server lets it on with any launcher");
        Check.False(MissileAmmo.CanShoot(2, 1), "arrows in a crossbow: no");
        Check.False(MissileAmmo.CanShoot(0, 0), "no launcher type: no");
        Check.False(MissileAmmo.Fits(2, 0), "but ammo of no type must come off before a launcher is wielded");

        var rank = MissileAmmo.RankBy("", null);
        var deadly = new AmmoCandidate(1, "Deadly Quarrel", 2, "Pierce", false, 300, -1, false);
        var inf = new AmmoCandidate(2, "Stormcaller's Sheaf", 2, "", false, 1, -1, false, Infinite: true);
        var untypedInf = new AmmoCandidate(3, "Quiver of Echoes", 0, "", false, 1, -1, false, Infinite: true);
        var untypedInf2 = new AmmoCandidate(4, "Sylvan Volley", 0, "", false, 1, -1, false, Infinite: true);
        var arrows = new AmmoCandidate(5, "Arrow", 1, "", false, 1, -1, false);
        Check.Eq(MissileAmmo.Choose(2, "", new List<AmmoCandidate> { deadly, inf }, rank)?.Id, 2, "infinite beats a better finite stack (it never runs out)");
        Check.Eq(MissileAmmo.Choose(2, "", new List<AmmoCandidate> { deadly, inf with { Infinite = false } }, rank)?.Id, 1, "a single item flagged not infinite: quality decides as before");
        Check.Eq(MissileAmmo.Choose(2, "", new List<AmmoCandidate> { untypedInf, inf }, rank)?.Id, 2, "the launcher's own type before ammo of no type");
        Check.Eq(MissileAmmo.Choose(2, "", new List<AmmoCandidate> { arrows, untypedInf }, rank)?.Id, 3, "ammo of no type is still a pick");
        Check.Eq(MissileAmmo.Choose(2, "", new List<AmmoCandidate> { untypedInf2, untypedInf }, rank, demoteId: 4)?.Id, 3, "not the stack just taken off the other launcher");
        Check.Eq(MissileAmmo.Choose(2, "", new List<AmmoCandidate> { deadly with { ListIndex = 2 }, inf }, rank)?.Id, 1, "a listed stack still comes first");
        Check.Null(MissileAmmo.Choose(2, "", new List<AmmoCandidate> { arrows }, rank), "only arrows: nothing for a crossbow");
    }

    // ── The simulated server ────────────────────────────────────────────────────

    private static int SlotOf(uint id) => FakeHost.Ints.TryGetValue((id, 9), out int s) ? s : 0;
    private static int TypeOf(uint id) => FakeHost.Ints.TryGetValue((id, 50), out int t) ? t : 0;
    private static bool On(uint id, int slot) => FakeHost.Equipped.TryGetValue(id, out uint at) && (at & slot) != 0;
    private static uint InSlot(int slot) => FakeHost.Equipped.Where(kv => (kv.Value & slot) != 0).Select(kv => kv.Key).FirstOrDefault();
    private static bool IsThrown(uint id) => FakeHost.Ints.TryGetValue((id, 11), out int max) && max > 1 && SlotOf(id) == MissileSlot;
    private static bool IsLauncher(uint id) => SlotOf(id) == MissileSlot && !IsThrown(id);

    private static void Wield(uint id, int slot)
    {
        FakeHost.Equipped[id] = (uint)slot;
        FakeHost.Ints[(id, 10)] = slot;
        EquippedState.Invalidate();
    }

    private static void Unwield(uint id)
    {
        FakeHost.Equipped.Remove(id);
        FakeHost.Ints.Remove((id, 10));
        EquippedState.Invalidate();
    }

    /// <summary>The fake server's view: can the character shoot now (a launcher and ammo it takes, or a thrown weapon)?</summary>
    private static bool ServerCanShoot()
    {
        uint main = InSlot(MissileSlot);
        if (main == 0) return false;
        if (IsThrown(main)) return true;
        uint ammo = InSlot(AmmoSlot);
        return ammo != 0 && (TypeOf(ammo) == 0 || TypeOf(main) == 0 || TypeOf(ammo) == TypeOf(main));
    }

    private sealed class Server
    {
        public double AnswerMs = 80, StanceMs = 400;
        /// <summary>Extra time before the server answers a use of this item (a slow server).</summary>
        public readonly Dictionary<uint, double> SlowUse = new();
        public readonly List<string> Refusals = new();
        private readonly List<(double At, Action Do)> _due = new();
        private int _moves, _wields, _uses, _modes;

        public void Observe(double now)
        {
            while (FakeHost.CombatModeRequests.Count > _modes)
            {
                int mode = FakeHost.CombatModeRequests[_modes++];
                _due.Add((now + StanceMs, () =>
                {
                    // ACE: a launcher with nothing it can shoot can't enter Missile mode.
                    if (mode == CombatMode.Missile && !ServerCanShoot()) { Refusals.Add("Missile mode with nothing to shoot"); return; }
                    FakeHost.CombatModeValue = mode;
                }));
            }
            while (FakeHost.Moves.Count > _moves)
            {
                uint id = FakeHost.Moves[_moves++].Id;
                _due.Add((now + AnswerMs, () =>
                {
                    bool wasAmmo = On(id, AmmoSlot);
                    Unwield(id);
                    // ACE TryDequipObjectWithNetworking: ammo off in Missile mode drops to peace (a thrown weapon keeps Missile).
                    if (wasAmmo && FakeHost.CombatModeValue == CombatMode.Missile && !IsThrown(InSlot(MissileSlot)))
                        FakeHost.CombatModeValue = CombatMode.NonCombat;
                }));
            }
            while (FakeHost.Wields.Count > _wields)
            {
                var w = FakeHost.Wields[_wields++];
                _due.Add((now + AnswerMs, () => TryWield(w.Id, (int)w.Mask, clientUse: false)));
            }
            while (FakeHost.Uses.Count > _uses)
            {
                uint id = FakeHost.Uses[_uses++];
                double extra = SlowUse.TryGetValue(id, out double x) ? x : 0;
                _due.Add((now + AnswerMs * 2 + extra, () => TryWield(id, SlotOf(id), clientUse: true)));
            }
        }

        public void Answer(double now)
        {
            foreach (var d in _due.Where(d => d.At <= now).ToList()) { d.Do(); _due.Remove(d); }
        }

        private void TryWield(uint id, int slot, bool clientUse)
        {
            if (slot == 0) return;
            // The client's own use takes off what's in the way in the hands (never the ammo: ACE's check below).
            if (clientUse && (slot & MainSlots) != 0)
                foreach (uint other in FakeHost.Equipped.Where(kv => kv.Key != id && (kv.Value & MainSlots) != 0).Select(kv => kv.Key).ToList()) Unwield(other);
            if (clientUse && slot == AmmoSlot && InSlot(AmmoSlot) is uint old && old != 0 && old != id)
                Unwield(old);
            if (slot == MissileSlot && IsLauncher(id))
            {
                uint ammo = InSlot(AmmoSlot);
                if (TypeOf(id) != 0 && ammo != 0 && TypeOf(ammo) != TypeOf(id)) { Refuse(id, $"launcher 0x{id:X8} with ammo 0x{ammo:X8} of type {TypeOf(ammo)} on"); return; }
            }
            if (slot == AmmoSlot)
            {
                uint main = InSlot(MissileSlot);
                if (main != 0 && TypeOf(main) != 0 && TypeOf(id) != 0 && TypeOf(main) != TypeOf(id)) { Refuse(id, $"ammo 0x{id:X8} of type {TypeOf(id)} at launcher type {TypeOf(main)}"); return; }
            }
            if (slot == HeldSlot && FakeHost.Equipped.Any(kv => kv.Key != id && (kv.Value & (MeleeSlot | ShieldSlot | MissileSlot | TwoHandedSlot)) != 0))
            { Refuse(id, "a caster with a hand full"); return; }
            if ((slot & MainSlots) != 0 && FakeHost.Equipped.Any(kv => kv.Key != id && (kv.Value & MainSlots) != 0))
            { Refuse(id, $"0x{id:X8} with the main hand full"); return; }
            Wield(id, slot);
        }

        private void Refuse(uint id, string why)
        {
            Refusals.Add(why);
            FakeHost.WeenieError(0x0029, 0x00A0, id);
        }
    }

    // ── The rig ───────────────────────────────────────────────────────────────

    private sealed class Sim : IDisposable
    {
        public readonly LegacyUiSettings S;
        public readonly CombatManager C;
        public readonly MonsterDamageStore Store = new();
        public readonly Server Sv = new();
        public readonly WeaponSwapGate Gate;
        public readonly WorldObject Target = new(I(Monster), "Drudge Skulker", AcObjectClass.Monster);
        public double SimMs;
        private static readonly DateTime T0 = new(2026, 11, 7, 20, 0, 0);
        public DateTime Now => T0.AddMilliseconds(SimMs);
        public readonly List<string> Trace = new();
        /// <summary>Times combat said "ready to attack" while the fake server had nothing to shoot.</summary>
        public int DryReady;
        private const BindingFlags NP = BindingFlags.Instance | BindingFlags.NonPublic;
        private readonly MethodInfo _equip = typeof(CombatManager).GetMethod("EquipWeaponAndSetStance", NP)!;
        private readonly MethodInfo _tickSwap = typeof(CombatManager).GetMethod("TickRearmSwap", NP)!;

        /// <param name="arrange">The items and what is on, before the host and cache read them.</param>
        public Sim(Action arrange, uint[] listed, int mode = CombatMode.Missile, bool fastSwap = true)
        {
            FakeHost.Reset();
            FakeHost.WeaponCalls = true;
            FakeHost.WieldCalls = true;
            FakeHost.EquippedCalls = true;
            FakeHost.CombatModeCalls = true;
            FakeHost.WeenieErrorCalls = true;
            FakeHost.CombatModeValue = mode;
            RynthClock.TestNow = () => Now;
            EquippedState.Clock = () => (long)SimMs + 100_000;
            ActionPacer.Enabled = false;

            Item(Bow, "Longbow", 0x100, MissileSlot, ammoType: 1);
            Item(Crossbow, "Heavy Crossbow", 0x100, MissileSlot, ammoType: 2);
            Item(Wand, "Wand", 0x8000, HeldSlot);
            arrange();
            FakeHost.Wcids[Monster] = Wcid;

            S = new LegacyUiSettings { IsMacroRunning = true, FastWeaponSwap = fastSwap, PeaceModeWhenIdle = false, WieldUnlistedWandWhenNoneListed = false };
            S.ItemRules = listed.Select(i => new ItemRule { Id = I(i), Name = FakeHost.Names[i], Element = "", ElementSource = "", Action = "Weapon" }).ToList();
            var host = FakeHost.Create(Player);
            var ids = new[] { Bow, Crossbow, Axe, Wand, InfArrows, InfQuarrels, DeadlyQuarrels, Arrows }.Where(FakeHost.Names.ContainsKey).ToList();
            var cache = FakeHost.MakeCache(host, Player, ids);
            C = new CombatManager(host, S, cache);
            C.SetPlayerId(Player);
            C.SetDamageStores(null, Store);
            C.CanCastOverride = _ => false;
            C.OpenPackOverride = () => I(Player);
            C.OffhandReadMs = 0;
            C.SwapTracker.MinDwellMs = 0;
            C.LastVitalCastAt = () => DateTime.MinValue;
            C.BuffingWantsTick = () => false;
            C.SwapBurstEnabled = () => false;
            Gate = new WeaponSwapGate { MinIntervalMs = 0, NowMs = () => (long)SimMs + 100_000 };
            C.SetWeaponSwapGate(Gate);
            typeof(CombatManager).GetField("_activeTargetId", NP)!.SetValue(C, I(Monster));
        }

        public void Dispose()
        {
            RynthClock.TestNow = null;
            EquippedState.Clock = null;
            EquippedState.Invalidate();
            ActionPacer.Enabled = false;
            ActionPacer.Reset();
        }

        public static void Item(uint id, string name, uint itemType, int validSlots, int ammoType = 0, int stack = 0, int maxStack = 0)
        {
            FakeHost.Names[id] = name;
            FakeHost.ItemTypes[id] = itemType;
            FakeHost.Ints[(id, 9)] = validSlots;
            if (ammoType != 0) FakeHost.Ints[(id, 50)] = ammoType;
            if (stack != 0) FakeHost.Ints[(id, 12)] = stack;
            if (maxStack != 0) FakeHost.Ints[(id, 11)] = maxStack;
        }

        /// <summary>The Damage tab's weapon for this monster.</summary>
        public void Wants(uint weapon) => Store.SetManualWeapon(Wcid, weapon);

        /// <summary>One plugin tick (25 ms): the server's answers, the hands-swap heartbeat, combat's equip.</summary>
        public bool Tick()
        {
            Sv.Answer(SimMs);
            _tickSwap.Invoke(C, null);
            bool ready = (bool)_equip.Invoke(C, new object[] { Target, "Auto" })!;
            if (ready && !ServerCanShoot()) DryReady++;
            Sv.Observe(SimMs);
            string line = $"mode={FakeHost.CombatModeValue} on={string.Join(",", FakeHost.Equipped.Keys.Select(k => FakeHost.Names[k]))} ready={ready}";
            if (Trace.Count == 0 || !Trace[^1].EndsWith(line, StringComparison.Ordinal)) Trace.Add($"{SimMs:0} {line}");
            SimMs += 25;
            return ready;
        }

        /// <summary>Ticks until combat is ready to attack in Missile mode (true) or <paramref name="maxMs"/> pass.</summary>
        public bool RunUntilShooting(double maxMs)
        {
            double end = SimMs + maxMs;
            while (SimMs <= end)
                if (Tick() && FakeHost.CombatModeValue == CombatMode.Missile) return true;
            return false;
        }

        public void Run(double ms) { double end = SimMs + ms; while (SimMs <= end) Tick(); }

        public string Why() =>
            Environment.NewLine + string.Join(Environment.NewLine, Trace.TakeLast(20)) + Environment.NewLine +
            string.Join(Environment.NewLine, FakeHost.Logs.Where(l => l.Contains("[EquipDiag]")).TakeLast(20)) +
            (Sv.Refusals.Count > 0 ? Environment.NewLine + "refused: " + string.Join("; ", Sv.Refusals) : "");
    }

    // ── The fights ────────────────────────────────────────────────────────────

    /// <summary>Infinite ammo as a custom server makes it: one item, no stack, a name of its own, its AmmoType set.</summary>
    private static void CustomInfinite()
    {
        Sim.Item(InfArrows, "Sylvan Volley", 0x100, AmmoSlot, ammoType: 1);
        Sim.Item(InfQuarrels, "Stormcaller's Sheaf", 0x100, AmmoSlot, ammoType: 2);
        Sim.Item(DeadlyQuarrels, "Deadly Quarrel", 0x100, AmmoSlot, ammoType: 2, stack: 80, maxStack: 250);
    }

    private static void OwnersReport()
    {
        using var sim = new Sim(() =>
        {
            CustomInfinite();
            Wield(Bow, MissileSlot);
            Wield(InfArrows, AmmoSlot);
        }, new[] { Bow, Crossbow });
        sim.Wants(Crossbow);

        Check.True(sim.RunUntilShooting(6000), "shooting with the crossbow within 6 s:" + sim.Why());
        Check.True(On(Crossbow, MissileSlot), "the crossbow in hand");
        Check.True(On(InfQuarrels, AmmoSlot), "the infinite quarrels on (over the Deadly finite ones):" + sim.Why());
        Check.False(On(InfArrows, AmmoSlot), "the infinite arrows off");
        Check.True(FakeHost.Moves.Count >= 1 && FakeHost.Moves[0].Id == InfArrows, "the arrows went into the pack first");
        int crossbowUse = FakeHost.Uses.IndexOf(Crossbow), quarrelUse = FakeHost.Uses.IndexOf(InfQuarrels);
        Check.True(crossbowUse >= 0 && quarrelUse > crossbowUse, "the crossbow, then its quarrels: " + string.Join(",", FakeHost.Uses.Select(u => FakeHost.Names[u])));
        Check.False(FakeHost.Uses.Contains(DeadlyQuarrels), "the finite quarrels were never wielded");
        Check.Eq(sim.Sv.Refusals.Count, 0, "the server refused nothing: " + string.Join("; ", sim.Sv.Refusals));
        Check.Eq(sim.DryReady, 0, "never ready to shoot with nothing to shoot");
        Check.True(FakeHost.Logs.Any(l => l.Contains("ammo in:") && l.Contains("Stormcaller")), "the ammo landing is logged");
        Check.False(sim.C.AmmoSwapInProgress, "crafting may run again");
    }

    private static void UntypedInfinite()
    {
        using var sim = new Sim(() =>
        {
            // Neither has an AmmoType or an ammo name: the ammunition slot is all that says what they are.
            Sim.Item(InfArrows, "Sylvan Volley", 0x100, AmmoSlot);
            Sim.Item(InfQuarrels, "Quiver of Echoes", 0x100, AmmoSlot);
            Wield(Bow, MissileSlot);
            Wield(InfArrows, AmmoSlot);
        }, new[] { Bow, Crossbow });
        sim.Wants(Crossbow);

        var pack = sim.C.PackAmmo();
        Check.Eq(pack.Count, 2, "both are ammo (the ammunition slot), no name or AmmoType needed");
        Check.True(pack.All(a => a.Untyped && a.Infinite), "of no type, and infinite (single items)");

        Check.True(sim.RunUntilShooting(6000), "shooting with the crossbow:" + sim.Why());
        Check.True(FakeHost.Moves.Any(m => m.Id == InfArrows), "the bow's ammo came off before the crossbow (ACE refuses a launcher with ammo of no type on)");
        Check.True(On(InfQuarrels, AmmoSlot), "the other one went on, not the one just taken off the bow:" + sim.Why());
        Check.False(FakeHost.Uses.Contains(InfArrows), "the bow's ammo was not put back on");
        Check.Eq(sim.Sv.Refusals.Count, 0, "nothing refused: " + string.Join("; ", sim.Sv.Refusals));
        Check.Eq(sim.DryReady, 0, "no dry fire");
    }

    private static void UnlimitedUseWins()
    {
        using var sim = new Sim(() =>
        {
            Sim.Item(InfQuarrels, "Quarrel", 0x100, AmmoSlot, ammoType: 2, stack: 50, maxStack: 250);
            Sim.Item(DeadlyQuarrels, "Deadly Quarrel", 0x100, AmmoSlot, ammoType: 2, stack: 80, maxStack: 250);
            Wield(Crossbow, MissileSlot);
        }, new[] { Crossbow });
        sim.Wants(Crossbow);

        var before = sim.C.PackAmmo();
        Check.False(before.Any(a => a.Infinite), "not appraised yet: two plain stacks");
        FakeHost.Bools[(InfQuarrels, 63)] = true;          // the appraisal: UnlimitedUse
        var after = sim.C.PackAmmo();
        Check.True(after.First(a => a.Id == I(InfQuarrels)).Infinite, "UnlimitedUse read: infinite");

        Check.True(sim.RunUntilShooting(4000), "shooting:" + sim.Why());
        Check.True(On(InfQuarrels, AmmoSlot), "the UnlimitedUse quarrels went on, over the Deadly ones");
    }

    private static void EmptyQuiverNotOurSwap()
    {
        using var sim = new Sim(() =>
        {
            CustomInfinite();
            Wield(Crossbow, MissileSlot);                    // put in hand by the player, or a meta: no ammo on
        }, new[] { Bow, Crossbow }, mode: CombatMode.NonCombat);
        sim.Wants(Crossbow);

        Check.True(sim.RunUntilShooting(4000), "shooting:" + sim.Why());
        Check.True(On(InfQuarrels, AmmoSlot), "combat wielded the quarrels itself");
        Check.Eq(FakeHost.Uses.Count(u => u == InfQuarrels), 1, "once");
        Check.Eq(FakeHost.CombatModeRequests.FirstOrDefault(), CombatMode.Missile, "Missile asked for only after the ammo");
        Check.Eq(sim.Sv.Refusals.Count, 0, "nothing refused: " + string.Join("; ", sim.Sv.Refusals));
        Check.Eq(sim.DryReady, 0, "no dry fire");
        Check.True(FakeHost.Logs.Any(l => l.Contains("none it can shoot was on")), "the log says why");
    }

    private static void AfterTheWindow()
    {
        using var sim = new Sim(() =>
        {
            CustomInfinite();
            Wield(Bow, MissileSlot);
            Wield(InfArrows, AmmoSlot);
        }, new[] { Bow, Crossbow });
        sim.Wants(Crossbow);
        // The server holds the crossbow's wield for 31 s (slow, not refusing: no resend meanwhile).
        sim.Sv.SlowUse[Crossbow] = 31_000;
        sim.C.SwapTracker.ResolveMs = 60_000;

        Check.True(RunUntil(sim, () => FakeHost.Uses.Contains(Crossbow), 4000), "the crossbow wield goes out:" + sim.Why());
        Check.True(RunUntil(sim, () => On(Crossbow, MissileSlot), 33_000), "the crossbow lands 31 s later:" + sim.Why());
        var until = (DateTime)typeof(CombatManager).GetField("_ammoFollowUpUntil", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(sim.C)!;
        Check.True(until < sim.Now, "the 30 s swap window is over");
        Check.Eq(FakeHost.Uses.Count(u => u == InfQuarrels), 0, "no quarrels before the crossbow landed");
        Check.True(sim.RunUntilShooting(4000), "its quarrels still go on, and it shoots:" + sim.Why());
        Check.True(On(InfQuarrels, AmmoSlot), "the infinite quarrels");
        Check.Eq(sim.DryReady, 0, "no dry fire");
    }

    private static bool RunUntil(Sim sim, Func<bool> until, double maxMs)
    {
        double end = sim.SimMs + maxMs;
        while (sim.SimMs <= end) { sim.Tick(); if (until()) return true; }
        return false;
    }

    private static void WrongAmmoInHand()
    {
        using var sim = new Sim(() =>
        {
            CustomInfinite();
            Wield(Crossbow, MissileSlot);
            Wield(InfArrows, AmmoSlot);                      // a hand swap on a server that let it be
        }, new[] { Bow, Crossbow }, mode: CombatMode.NonCombat);
        sim.Wants(Crossbow);

        var has = typeof(CombatManager).GetMethod("HasWieldedAmmo", BindingFlags.Instance | BindingFlags.NonPublic)!;
        Check.False((bool)has.Invoke(sim.C, null)!, "arrows with a crossbow in hand are not ammo to shoot (a dry fire before)");

        Check.True(sim.RunUntilShooting(5000), "shooting:" + sim.Why());
        Check.True(FakeHost.Moves.Any(m => m.Id == InfArrows), "the arrows into the pack");
        Check.True(On(InfQuarrels, AmmoSlot), "then the quarrels");
        Check.Eq(sim.Sv.Refusals.Count, 0, "nothing refused: " + string.Join("; ", sim.Sv.Refusals));
        Check.Eq(sim.DryReady, 0, "no dry fire");
        Check.Eq(FakeHost.CombatModeRequests.Count(m => m == CombatMode.Missile), 1, "one Missile request, after the quarrels");
    }

    private static void NoAmmoSaysOnce()
    {
        using var sim = new Sim(() =>
        {
            Sim.Item(Arrows, "Arrow", 0x100, AmmoSlot, ammoType: 1, stack: 100, maxStack: 250);
            Wield(Crossbow, MissileSlot);
        }, new[] { Crossbow }, mode: CombatMode.NonCombat);
        sim.Wants(Crossbow);
        sim.S.EnableMissileCrafting = false;

        sim.Run(3000);
        Check.Eq(FakeHost.Chat.Count(c => c.Contains("No quarrels for Heavy Crossbow")), 1, "said in chat once: " + string.Join(" | ", FakeHost.Chat));
        Check.Eq(FakeHost.Uses.Count, 0, "no wield (arrows don't fit a crossbow)");
        Check.Eq(FakeHost.CombatModeRequests.Count, 0, "no Missile request");
        Check.Eq(sim.DryReady, 0, "never ready to shoot");
        Check.Eq(sim.C.LastCombatSkipReason, "no-ammo", "the hold is the no-ammo one (not a stall)");

        // Quarrels turn up (looted, bought, given): they go on.
        Sim.Item(InfQuarrels, "Stormcaller's Sheaf", 0x100, AmmoSlot, ammoType: 2);
        var cache = (WorldObjectCache)typeof(CombatManager).GetField("_worldFilter", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(sim.C)!;
        _ = cache[I(InfQuarrels)];
        ForceRescan(cache);
        Check.True(sim.RunUntilShooting(4000), "the new quarrels go on and it shoots:" + sim.Why());
        Check.True(On(InfQuarrels, AmmoSlot), "on");

        // Gone again: said again, once.
        Unwield(InfQuarrels);
        FakeHost.Names.Remove(InfQuarrels);
        FakeHost.Ints.Remove((InfQuarrels, 9));
        FakeHost.Ints.Remove((InfQuarrels, 50));
        ForceRescan(cache);
        sim.Run(2000);
        Check.Eq(FakeHost.Chat.Count(c => c.Contains("No quarrels for Heavy Crossbow")), 2, "said again after it ran out, once");
    }

    private static void ForceRescan(WorldObjectCache cache) =>
        typeof(WorldObjectCache).GetField("_lastDirectInventoryScan", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(cache, DateTime.MinValue);

    private static void ThrownWeapon()
    {
        using var sim = new Sim(() =>
        {
            Sim.Item(Arrows, "Arrow", 0x100, AmmoSlot, ammoType: 1, stack: 100, maxStack: 250);
            Sim.Item(Axe, "Throwing Axe", 0x100, MissileSlot, stack: 30, maxStack: 50);
            Wield(Bow, MissileSlot);
            Wield(Arrows, AmmoSlot);
        }, new[] { Bow, Axe });
        sim.Wants(Axe);

        Check.True(sim.RunUntilShooting(5000), "throwing with the axe:" + sim.Why());
        Check.True(On(Axe, MissileSlot), "the axe in hand");
        Check.False(On(Arrows, AmmoSlot), "the arrows are not left on");
        Check.True(FakeHost.Moves.Any(m => m.Id == Arrows), "the arrows went into the pack");
        Check.True(FakeHost.Uses.IndexOf(Axe) >= 0, "the axe was wielded");
        Check.False(FakeHost.Uses.Contains(Arrows), "no ammo wielded for the thrown weapon");
        Check.Eq(sim.Sv.Refusals.Count, 0, "nothing refused: " + string.Join("; ", sim.Sv.Refusals));
        Check.Eq(sim.DryReady, 0, "no dry fire");

        // Back to the bow: the arrows go on after it.
        sim.Wants(Bow);
        Check.True(RunUntil(sim, () => On(Bow, MissileSlot) && On(Arrows, AmmoSlot), 5000), "the bow and its arrows:" + sim.Why());
        Check.True(sim.RunUntilShooting(3000), "shooting with the bow again:" + sim.Why());
        Check.True(FakeHost.Uses.LastIndexOf(Arrows) > FakeHost.Uses.LastIndexOf(Bow), "the bow, then the arrows");
        Check.Eq(sim.DryReady, 0, "no dry fire");
    }

    private static void FastSwapFromWand()
    {
        using var sim = new Sim(() =>
        {
            CustomInfinite();
            Wield(Wand, HeldSlot);                           // healing: the wand in hand, the bow's arrows still on
            Wield(InfArrows, AmmoSlot);
        }, new[] { Bow, Crossbow }, mode: CombatMode.Magic);
        sim.Wants(Crossbow);

        Check.True(sim.RunUntilShooting(6000), "shooting with the crossbow:" + sim.Why());
        Check.True(FakeHost.Logs.Any(l => l.Contains("weapons for the fight")), "Fast Weapon Swap's hands swap ran:" + sim.Why());
        Check.True(FakeHost.Moves.Any(m => m.Id == InfArrows), "the arrows into the pack");
        Check.True(FakeHost.Moves.Any(m => m.Id == Wand), "the wand into the pack");
        Check.True(FakeHost.Wields.Any(w => w.Id == Crossbow && w.Mask == (uint)MissileSlot), "the crossbow wielded into the missile slot:" + sim.Why());
        Check.True(On(InfQuarrels, AmmoSlot), "then its infinite quarrels");
        Check.Eq(sim.Sv.Refusals.Count, 0, "nothing refused (no crossbow with the arrows on): " + string.Join("; ", sim.Sv.Refusals));
        Check.Eq(sim.DryReady, 0, "no dry fire");
        Check.Eq(sim.Gate.HeldBy, "", "the swap gate is free");
    }

    /// <summary>
    /// A bow -> crossbow swap was cut short by a heal (the arrows already in the pack, the wand on), and
    /// weapons back after heals put the last fight's bow back: that swap moves hands only, never ammo,
    /// and no launcher swap of combat's armed an ammo step, so the bow stood with an empty quiver.
    /// </summary>
    private static void WeaponsBackAfterHeals()
    {
        foreach (bool fast in new[] { true, false })
        {
            using var sim = new Sim(() =>
            {
                Sim.Item(Arrows, "Arrow", 0x100, AmmoSlot, ammoType: 1, stack: 100, maxStack: 250);
                Wield(Wand, HeldSlot);
            }, new[] { Bow, Crossbow, Wand }, mode: CombatMode.Magic, fastSwap: fast);
            const BindingFlags F = BindingFlags.Instance | BindingFlags.NonPublic;
            sim.S.RearmAfterVitals = true;
            typeof(CombatManager).GetField("_fightMainId", F)!.SetValue(sim.C, I(Bow));
            DateTime heal = sim.Now.AddSeconds(-2);
            sim.C.LastVitalCastAt = () => heal;
            typeof(CombatManager).GetField("_activeTargetId", F)!.SetValue(sim.C, 0);
            var step = typeof(CombatManager).GetMethod("RearmAfterVitalsStep", F)!;
            var tick = typeof(CombatManager).GetMethod("TickRearmSwap", F)!;
            for (int i = 0; i < 200 && !On(Bow, MissileSlot); i++)
            {
                sim.Sv.Answer(sim.SimMs);
                tick.Invoke(sim.C, null);
                step.Invoke(sim.C, null);
                sim.Sv.Observe(sim.SimMs);
                sim.SimMs += 25;
            }
            string label = fast ? "Fast Weapon Swap on" : "Fast Weapon Swap off";
            Check.True(On(Bow, MissileSlot), $"{label}: weapons back put the bow in hand:" + sim.Why());
            Check.False(On(Arrows, AmmoSlot), $"{label}: its arrows are still in the pack (weapons back moves hands only)");

            // A monster: combat's equip finds the bow with nothing to shoot, and puts the arrows on.
            typeof(CombatManager).GetField("_activeTargetId", F)!.SetValue(sim.C, I(Monster));
            sim.Wants(Bow);
            Check.True(sim.RunUntilShooting(4000), $"{label}: shooting with the bow:" + sim.Why());
            Check.True(On(Arrows, AmmoSlot), $"{label}: the arrows went on");
            Check.Eq(sim.DryReady, 0, $"{label}: no dry fire");
        }
    }

    private static void LauncherNotItsOwnAmmo()
    {
        using var sim = new Sim(() =>
        {
            Sim.Item(Crossbow, "Thunderbolt Arbalest", 0x100, MissileSlot, ammoType: 2);
            Wield(Crossbow, MissileSlot);
        }, new[] { Crossbow });
        var has = typeof(CombatManager).GetMethod("HasWieldedAmmo", BindingFlags.Instance | BindingFlags.NonPublic)!;
        Check.False((bool)has.Invoke(sim.C, null)!, "an empty quiver, whatever the launcher's name says");
        sim.Wants(Crossbow);
        sim.Run(1500);
        Check.Eq(sim.DryReady, 0, "no shot");
    }

    // ── Ammo crafting ─────────────────────────────────────────────────────────

    private static MissileCraftingManager CraftRig(bool sheafOn, out WorldObjectCache cache)
    {
        ActionPacer.Enabled = false;
        ActionPacer.Reset();
        FakeHost.Reset();
        FakeHost.WeaponCalls = true;
        FakeHost.SalvageCalls = true;      // GetObjectWielderInfo (answers from Wielded)
        FakeHost.Names[Player] = "Archer";
        Sim.Item(Crossbow, "Siege Arbalest", 0x100, MissileSlot, ammoType: 2);    // no "crossbow" in the name
        Sim.Item(InfQuarrels, "Stormcaller's Sheaf", 0x100, AmmoSlot, ammoType: 2);
        Sim.Item(Arrows, "Arrow", 0x100, AmmoSlot, ammoType: 1, stack: 100, maxStack: 250);
        FakeHost.Wielded[Crossbow] = MissileSlot;
        FakeHost.Containers[Arrows] = Player;
        if (sheafOn) FakeHost.Wielded[InfQuarrels] = AmmoSlot;
        else FakeHost.Containers[InfQuarrels] = Player;
        var host = FakeHost.Create(Player);
        cache = FakeHost.MakeCache(host, Player, new[] { Crossbow, InfQuarrels, Arrows });
        var cm = new MissileCraftingManager(host, new LegacyUiSettings { EnableMissileCrafting = true });
        cm.SetObjectCache(cache);
        return cm;
    }

    private static void CraftStep(MissileCraftingManager cm, WorldObjectCache cache)
    {
        const BindingFlags F = BindingFlags.Instance | BindingFlags.NonPublic;
        if (cm.State == MissileCraftingManager.CraftState.Idle) typeof(MissileCraftingManager).GetField("_lastAmmoCheck", F)!.SetValue(cm, DateTime.MinValue);
        else typeof(MissileCraftingManager).GetField("_phaseStart", F)!.SetValue(cm, DateTime.Now.AddSeconds(-1));
        ForceRescan(cache);
        EquippedState.Invalidate();
        cm.ProcessCrafting();
    }

    private static void CraftingKnowsTypes()
    {
        var cm = CraftRig(sheafOn: true, out var cache);
        for (int i = 0; i < 3; i++) CraftStep(cm, cache);
        Check.Eq(cm.State, MissileCraftingManager.CraftState.Idle, "quarrels on: nothing to do");
        Check.Eq(FakeHost.Uses.Count, 0, "no arrows wielded at the arbalest (read as a bow by its name before)");
        Check.False(FakeHost.Logs.Any(l => l.Contains("MissileCraft: no")), "no evaluation every 5 s");
    }

    private static void CraftingWieldsOddNamed()
    {
        var cm = CraftRig(sheafOn: false, out var cache);
        for (int i = 0; i < 3 && !FakeHost.Uses.Any(); i++) CraftStep(cm, cache);
        Check.True(FakeHost.Logs.Any(l => l.Contains("MissileCraft: no Quarrel") && l.Contains("'Siege Arbalest'")), "the arbalest is a crossbow (its AmmoType): " + string.Join(" | ", FakeHost.Logs.Where(l => l.Contains("MissileCraft"))));
        Check.Eq(FakeHost.Uses.LastOrDefault(), InfQuarrels, "the odd-named quarrels are wielded, not the arrows");
        Check.True(cm.LastAmmoEquipAt != DateTime.MinValue, "the wield time is kept (combat waits a moment after it)");
    }
}

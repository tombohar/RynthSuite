using System;
using System.Linq;
using System.Reflection;
using RynthCore.Plugin.RynthAi;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.PluginSdk;
using RynthCore.RynthAiHostTests.Fakes;
using RynthCore.RynthAiTests.Harness;

namespace RynthCore.RynthAiHostTests.Tests;

// Kits are the standard heal, and the shield stays on.
// A vital spell out of Magic mode with no caster in hand starts no weapon swap while a kit or potion
// for that vital is carried (BuffManager.VitalSpellWouldSwap); buffing still swaps.
internal static class VitalWandGateTests
{
    private const uint Player = 0x500000B7;
    private const uint Wand = 0x80071001;
    private const uint Kit = 0x80071002;
    private const uint StamKit = 0x80071003;
    private const uint ManaPot = 0x80071004;
    private const uint HealPot = 0x80071005;
    private const uint CurrentWieldedLocation = 10;   // STypeInt CurrentWieldedLocation
    private const uint HeldSlot = 0x01000000;

    public static void Register(Runner r)
    {
        r.Add("vital wand gate: out of Magic with a healing kit carried, Heal Self starts no wand swap (not handled)", HealRefusedWithKit);
        r.Add("vital wand gate: the refused Heal Self falls through to the kit despite Kit Min Success", KitDespiteMinSuccessAfterRefusal);
        r.Add("vital wand gate: a caster already wielded in melee stance: the Magic stance change only, no weapon move", StanceOnlyWithCasterWielded);
        r.Add("vital wand gate: no kit and no potion at all: the swap is allowed (the only heal there is)", SwapAllowedWithNothingElse);
        r.Add("vital wand gate: Revitalize with a stamina kit, Stamina to Mana in a fight, the script heal with a health potion: no swap", OtherVitalsRefused);
        r.Add("vital wand gate: a pure caster in Magic mode with a kit carried still casts Heal Self", PureCasterUnaffected);
        r.Add("vital wand gate: buffing still swaps to the wand with a kit carried", BuffsStillSwap);
        r.Add("vital wand gate: Revitalize refused, no stamina potion: the stamina kit is used despite Kit Min Success", StaminaKitDespiteMinSuccess);
        r.Add("vital wand gate: Stamina to Mana never swaps in a fight; out of one with no mana potion it may", ManaInFightNoSwap);
        r.Add("vital wand gate: in Magic stance with no wand in hand and a kit carried, Heal Self is not gated", MagicNoWandNotGated);
        r.Add("vital wand gate: a kit its line called used up, or gone from the client's pack, is no kit: Heal Self is handled and the swap is allowed", GoneKitAllowsSwap);
        r.Add("vital wand gate: a swap already under way finishes when a kit reappears (no abandoned swap, no kit mid-wield)", SwapInFlightFinishes);
        r.Add("vital wand gate: a kit the server refused 3 times within 10 s is no kit for the gate; a kit landing counts it again", RefusedKitAllowsSwap);
        r.Add("vital wand gate: the mana step's stamina refill also uses the stamina kit despite Kit Min Success", ManaRefillStaminaLastResort);
        r.Add("vital wand gate: out of a fight Stamina to Mana may swap with a mana potion carried; in a fight it may not", ManaPotionOutOfFightSwaps);
        r.Add("vital wand gate: a refusal streak ages out: 11 s after the last refusal the kit counts again", RefusedStreakAgesOut);
        r.Add("vital wand gate: no wand found: Heal Self takes the bare-handed Magic stance, not gated", NoWandNotGated);
        r.Add("vital wand gate: one line when it starts refusing, one when the swap may go again or nothing asks for 5 s, one for a refused kit", GateLogLines);
        r.Add("vital wand gate: under Emergency Heal At the kit despite Kit Min Success still goes; the HP failsafe's recall still swaps to the wand", EmergencyFloorKept);
        r.Add("vital wand gate: the between-fights top-off (no Revitalize) uses a stamina kit under Stamina Kit Min Success as the last resort", TopOffStaminaLastResort);
        r.Add("vital wand gate: at or under Emergency Heal At a health spell may swap to the wand with a kit carried (the surest heal goes)", EmergencySpellMaySwap);
        r.Add("buffs wait: under Heal At in a fight no buff is cast and the arbiter isn't asked for buffs; over it or out of a fight they go on", BuffsWaitWhileHurt);
    }

    // 2026-11-07: at 9% the gate sent Drakkon's kit (5-8% at low health with vitae) instead of Stamina to Health, and he died.
    private static void EmergencySpellMaySwap()
    {
        var b = Rig(CombatMode.Melee, new[] { Kit });
        S.EmergencyHealAt = 30;
        SetVitals(b, 9, 74, 100);
        Check.True(VitalCast(b, "Stamina to Health Self"), "9% health, kit carried: Stamina to Health is handled: " + Why(b));
        Check.True(SwapActive(b), "its wand swap is under way");

        var a = Rig(CombatMode.Melee, new[] { Kit });
        S.EmergencyHealAt = 30;
        SetVitals(a, 45, 74, 100);
        Check.False(VitalCast(a, "Heal Self"), "45%, over Emergency Heal At: the gate still refuses Heal Self");
        Check.True(Why(a).Contains("no wand swap for a vital spell", StringComparison.Ordinal), "with its reason: " + Why(a));
    }

    // 2026-11-07: four weapon auras in a row with a golem hitting him under Heal At; the emergency heal waited for the last cast.
    private static void BuffsWaitWhileHurt()
    {
        var b = Rig(CombatMode.Melee, Array.Empty<uint>());
        S.HealAt = 60;
        b.InCombatOverride = () => true;
        SetVitals(b, 54, 100, 100);
        Check.True(b.BuffsWaitForHealth(), "54% under Heal At 60% in a fight: buffs wait");
        Check.True(FakeHost.Logs.Any(l => l.Contains("[Buff] buffs wait: health 54% is under Heal At 60% in a fight", StringComparison.Ordinal)), "logged once at the start");
        Check.True(b.BuffsWaitForHealth(), "still waiting");
        Check.Eq(FakeHost.Logs.Count(l => l.Contains("[Buff] buffs wait:", StringComparison.Ordinal)), 1, "not logged again");
        SetVitals(b, 70, 100, 100);
        Check.False(b.BuffsWaitForHealth(), "back over Heal At: buffs go on");
        Check.True(FakeHost.Logs.Any(l => l.Contains("[Buff] buffs go on", StringComparison.Ordinal)), "the end is logged");
        SetVitals(b, 40, 100, 100);
        b.InCombatOverride = () => false;
        Check.False(b.BuffsWaitForHealth(), "out of a fight: buffs go on even under Heal At");
    }

    private static void GateLogLines()
    {
        var b = Rig(CombatMode.Melee, new[] { Kit });
        DateTime t0 = new DateTime(2026, 1, 1, 18, 0, 0);
        DateTime now = t0;
        b.Clock = () => now;
        VitalCast(b, "Heal Self");
        VitalCast(b, "Heal Self");
        Check.Eq(FakeHost.Logs.Count(l => l.Contains("[RynthAi] Vitals: wand gate: no wand swap for Heal Self - a kit is carried", StringComparison.Ordinal)), 1,
            "the start is logged once: " + string.Join(" | ", FakeHost.Logs.Where(l => l.Contains("wand gate", StringComparison.Ordinal))));
        now = t0.AddSeconds(6);
        b.OnHeartbeat();
        Check.True(FakeHost.Logs.Any(l => l.Contains("wand gate ended for Heal Self: not asked for in 5 s", StringComparison.Ordinal)), "5 s with no refusal: the end is logged");

        // The swap allowed again (the kit gone) ends it at once.
        var g = Rig(CombatMode.Melee, new[] { Kit });
        VitalCast(g, "Heal Self");
        Check.True((bool)typeof(BuffManager).GetMethod("AttemptHealthKitUse", Np, new[] { typeof(bool) })!.Invoke(g, new object[] { false })!, "the kit goes out");
        g.OnChatWindowText("You heal yourself for 12 Health points. Your Treated Healing Kit is used up.", 0);
        Check.True(VitalCast(g, "Heal Self"), "the kit used up: Heal Self handled");
        Check.True(FakeHost.Logs.Any(l => l.Contains("wand gate ended for Heal Self: no healing kit or potion is carried; its wand swap may go", StringComparison.Ordinal)),
            "the end is logged with why");

        // A kit refused 3 times stops counting, logged.
        var k = Rig(CombatMode.Melee, new[] { Kit }, kitAnswers: true);
        FakeHost.UseDoneSeq = 10;
        RefuseKit(k, Kit); RefuseKit(k, Kit); RefuseKit(k, Kit);
        Check.Eq(FakeHost.Logs.Count(l => l.Contains("refused 3 times in 10 s: it no longer counts as a heal", StringComparison.Ordinal)), 1, "the streak is logged once");
    }

    // The safety floor: the gate only refuses spells in the vitals chain. The last-resort kit still goes
    // at Emergency Heal At, and the HP failsafe's Lifestone Recall (EnsureMagicMode, macro off) still swaps.
    private static void EmergencyFloorKept()
    {
        // 2026-11-07: under Emergency Heal At a kit under Kit Min Success no longer beats the heal spell's wand
        // swap (Drakkon's kit was 5-8% at low health and he died at 9% with it).
        var b = Rig(CombatMode.Melee, new[] { Kit }, kitMinSuccess: 95);
        S.EmergencyHealAt = 30;
        SetVitals(b, 20, 100, 100);
        Check.True(b.CheckVitals(), "20% under Emergency Heal At 30%: the vitals act");
        Check.Eq(FakeHost.UsesOn.Count, 0, "no kit under Kit Min Success");
        Check.True(SwapActive(b), "the heal spell's wand swap goes instead");

        // A kit at Kit Min Success is still the heal at Emergency Heal At.
        var k = Rig(CombatMode.Melee, new[] { Kit }, kitMinSuccess: 0);
        S.EmergencyHealAt = 30;
        SetVitals(k, 20, 100, 100);
        Check.True(k.CheckVitals(), "20%, a kit that passes Kit Min Success: the vitals act");
        Check.Eq(FakeHost.UsesOn.Count, 1, "the kit goes out");
        Check.Eq(FakeHost.Casts.Count, 0, "no spell");

        var r = Rig(CombatMode.Melee, new[] { Kit, HealPot }, usePotions: true);
        int before = SwapActivity();
        string wait = (string)typeof(BuffManager).GetMethod("RecallCastHolds", Np)!.Invoke(r, null)!;
        Check.Eq(wait, "wand swap / Magic stance", "the recall waits for its wand swap");
        Check.True(SwapActivity() > before, "the recall's wand swap starts with a kit and a potion carried");
    }

    // b2e1ade4: between fights out of Magic no Revitalize is cast for Top Off Stamina; kits still top off,
    // and with this port a stamina kit under Stamina Kit Min Success % is that last resort.
    private static void TopOffStaminaLastResort()
    {
        var b = Rig(CombatMode.Melee, new[] { StamKit }, kitMinSuccess: 95);
        b.InCombatOverride = () => false;
        S.TopOffStam = 60; S.RestamAt = 10;
        SetVitals(b, 100, 40, 100);
        Check.True(b.CheckVitals(), "stamina 40% under Top Off Stamina 60%, no fight: acts");
        Check.Eq(FakeHost.UsesOn.Count, 1, "one kit use");
        Check.Eq(FakeHost.UsesOn[0].Source, StamKit, "the stamina kit");
        Check.Eq(FakeHost.Casts.Count, 0, "no Revitalize");
        Check.Eq(FakeHost.CombatModeRequests.Count(m => m == CombatMode.Magic), 0, "no Magic stance");
    }

    // Out of a fight buffing is about to swap to the wand anyway, so a
    // mana potion drunk to spare the swap is a potion wasted. In a fight the refusal stands.
    private static void ManaPotionOutOfFightSwaps()
    {
        var q = Rig(CombatMode.Melee, new[] { ManaPot }, usePotions: true);
        q.InCombatOverride = () => false;
        Check.True(VitalCast(q, "Stamina to Mana Self"), "out of a fight, mana potion carried: handled: " + Why(q));
        Check.True(SwapActive(q), "the wand swap is under way");

        var f = Rig(CombatMode.Melee, new[] { ManaPot }, usePotions: true);
        f.InCombatOverride = () => true;
        Check.False(VitalCast(f, "Stamina to Mana Self"), "in a fight, mana potion carried: not handled");
        Check.True(Why(f).Contains("Stamina to Mana in a fight", StringComparison.Ordinal), "the in-fight reason: " + Why(f));
        Check.Eq(SwapActivity(), 0, "in a fight: no swap");
    }

    // The stamina last resort covers every stamina path: the stamina step, the mana step's refill
    // (AttemptManaRefill) and the buffing mana floor.
    private static void ManaRefillStaminaLastResort()
    {
        var b = Rig(CombatMode.Melee, new[] { StamKit }, kitMinSuccess: 95);
        SetVitals(b, 100, 10, 10);   // stamina 10% <= the Stamina to Mana floor (15): refill stamina first
        object[] args = { 10, false };
        bool acted = (bool)typeof(BuffManager).GetMethod("AttemptManaRefill", Np)!.Invoke(b, args)!;
        Check.True(acted, "the mana step acts");
        Check.True((bool)args[1], "by refilling stamina");
        Check.Eq(FakeHost.UsesOn.Count, 1, "one kit use");
        Check.Eq(FakeHost.UsesOn[0].Source, StamKit, "the stamina kit Kit Min Success skipped");
        Check.True(FakeHost.Logs.Any(l => l.Contains("using the stamina kit despite Stamina Kit Min Success", StringComparison.Ordinal)), "logged");
        Check.Eq(SwapActivity(), 0, "no swap");
    }

    /// <summary>The kit sent and refused by the server (a UseDone with an error code).</summary>
    private static void RefuseKit(BuffManager b, uint kit, bool stamina = false)
    {
        typeof(BuffManager).GetField("_kitRefusedAt", Np)!.SetValue(b, DateTime.MinValue);   // past KitRetryAfterRefusalMs (real clock)
        int uses = FakeHost.UsesOn.Count;
        Check.True((bool)typeof(BuffManager).GetMethod(stamina ? "AttemptStaminaKitUse" : "AttemptHealthKitUse", Np,
            stamina ? Type.EmptyTypes : new[] { typeof(bool) })!.Invoke(b, stamina ? null : new object[] { false })!, "the kit goes out");
        Check.Eq(FakeHost.UsesOn.Count, uses + 1, "sent");
        Check.Eq(FakeHost.UsesOn[^1].Source, kit, "that kit");
        FakeHost.UseDone(0x1D);   // YoureTooBusy
        Check.False(b.KitPending(), "refused: the wait ends");
    }

    // A kit the server keeps refusing (a UseDone error, "You're too busy!") is no answer the kit wait
    // drops: without a cap it would count for the gate forever and no spell heal could ever go.
    private static void RefusedKitAllowsSwap()
    {
        var setNow = new Action<BuffManager, DateTime>((bm, t) => bm.Clock = () => t);
        var t0 = new DateTime(2026, 1, 1, 18, 0, 0);

        var b = Rig(CombatMode.Melee, new[] { Kit }, kitAnswers: true);
        FakeHost.UseDoneSeq = 10;
        RefuseKit(b, Kit);
        RefuseKit(b, Kit);
        Check.False(VitalCast(b, "Heal Self"), "2 refusals: the kit still counts, Heal Self refused");
        RefuseKit(b, Kit);
        Check.True(VitalCast(b, "Heal Self"), "3 refusals within 10 s: the kit is no kit, Heal Self handled: " + Why(b));
        Check.True(SwapActive(b), "the wand swap is under way");

        // Spread over more than 10 s: no streak.
        var s = Rig(CombatMode.Melee, new[] { Kit }, kitAnswers: true);
        FakeHost.UseDoneSeq = 10;
        setNow(s, t0); RefuseKit(s, Kit);
        setNow(s, t0.AddSeconds(6)); RefuseKit(s, Kit);
        setNow(s, t0.AddSeconds(12)); RefuseKit(s, Kit);
        Check.False(VitalCast(s, "Heal Self"), "3 refusals over 12 s: the kit still counts");

        // A kit landing ends the streak.
        var l = Rig(CombatMode.Melee, new[] { Kit }, kitAnswers: true);
        FakeHost.UseDoneSeq = 10;
        RefuseKit(l, Kit); RefuseKit(l, Kit); RefuseKit(l, Kit);
        typeof(BuffManager).GetField("_kitRefusedAt", Np)!.SetValue(l, DateTime.MinValue);
        typeof(BuffManager).GetMethod("AttemptHealthKitUse", Np, new[] { typeof(bool) })!.Invoke(l, new object[] { false });
        l.OnChatWindowText("You heal yourself for 40 Health points. Your Treated Healing Kit has 3 uses left.", 0);
        Check.False(VitalCast(l, "Heal Self"), "a kit landed: it counts again, Heal Self refused");

        // Stamina kits too: Revitalize may swap once the stamina kit is refused 3 times.
        var st = Rig(CombatMode.Melee, new[] { StamKit }, kitMinSuccess: 0, kitAnswers: true);
        FakeHost.UseDoneSeq = 10;
        Check.False(VitalCast(st, "Revitalize Self"), "control: the stamina kit carried, Revitalize refused");
        RefuseKit(st, StamKit, stamina: true); RefuseKit(st, StamKit, stamina: true); RefuseKit(st, StamKit, stamina: true);
        Check.True(VitalCast(st, "Revitalize Self"), "stamina kit refused 3 times: Revitalize handled: " + Why(st));
    }

    // The gate allowed a swap (no kit), then a kit came back. Refusing now would leave the
    // swap untouched until TickWandSwap abandons it, with the shield maybe already in the pack.
    private static void SwapInFlightFinishes()
    {
        var b = Rig(CombatMode.Melee, Array.Empty<uint>());
        Check.True(VitalCast(b, "Heal Self"), "no kit: Heal Self handled");
        Check.True(SwapActive(b), "the swap started");
        var cache = (WorldObjectCache)typeof(BuffManager).GetField("_worldObjectCache", Np)!.GetValue(b)!;
        _ = cache[unchecked((int)Kit)];   // the kit is back in the pack
        cache.GetDirectInventory(forceRefresh: true);   // the fake host has no container walk: refresh the snapshot by hand
        Check.True((bool)typeof(BuffManager).GetMethod("HasUsableHealthKit", Np)!.Invoke(b, null)!, "the rig: the kit counts as usable");
        Check.True(VitalCast(b, "Heal Self"), "the kit back, the swap under way: still handled: " + Why(b));
        Check.True(SwapActive(b), "the swap is still under way");
        Check.Eq(FakeHost.UsesOn.Count, 0, "no kit sent mid-swap");
    }

    private static bool SwapActive(BuffManager b)
    {
        object swap = typeof(BuffManager).GetField("_wandSwap", Np)!.GetValue(b)!;
        return (bool)swap.GetType().GetProperty("Active")!.GetValue(swap)!;
    }

    // A wand not in hand is no reason to gate
    // a character already in Magic: no weapon moves for its cast.
    private static void MagicNoWandNotGated()
    {
        var b = Rig(CombatMode.Magic, new[] { Kit });
        VitalCast(b, "Heal Self");
        Check.False(Why(b).Contains("no wand swap for a vital spell", StringComparison.Ordinal), "in Magic: not gated: " + Why(b));
    }

    // A kit the kit picks would not use must not count for the gate either: one its result line called
    // used up (BuffManager.KitUsedUp.cs), or one the client's live pack walk no longer lists.
    private static void GoneKitAllowsSwap()
    {
        var b = Rig(CombatMode.Melee, new[] { Kit });
        Check.False(VitalCast(b, "Heal Self"), "control: the kit carried, Heal Self refused");

        var u = Rig(CombatMode.Melee, new[] { Kit });
        Check.True((bool)typeof(BuffManager).GetMethod("AttemptHealthKitUse", Np, new[] { typeof(bool) })!.Invoke(u, new object[] { false })!, "the kit goes out");
        u.OnChatWindowText("You heal yourself for 12 Health points. Your Treated Healing Kit is used up.", 0);
        Check.True(VitalCast(u, "Heal Self"), "the kit's line said used up: Heal Self handled: " + Why(u));
        Check.True(SwapActive(u), "the wand swap is under way");

        var w = Rig(CombatMode.Melee, new[] { Kit }, walk: true);
        var wc = (WorldObjectCache)typeof(BuffManager).GetField("_worldObjectCache", Np)!.GetValue(w)!;
        DateTime wt = new DateTime(2026, 1, 1, 18, 0, 0);
        wc.WalkClock = () => wt;                   // the walk's reuse on a test clock
        Check.False(VitalCast(w, "Heal Self"), "control, live walk: the kit carried, Heal Self refused");
        FakeHost.Containers.Remove(Kit);           // used up: the client deletes it; the cache never hears
        FakeHost.Names.Remove(Kit);
        wt = wt.AddMilliseconds(1100);             // past the walk's 1 s reuse
        Check.True(VitalCast(w, "Heal Self"), "the kit gone from the client's pack: Heal Self handled: " + Why(w));
        Check.True(SwapActive(w), "the wand swap is under way");
    }

    // A refusal streak ages out: with no refusal for 10 s the kit counts for the gate again.
    private static void RefusedStreakAgesOut()
    {
        var b = Rig(CombatMode.Melee, new[] { Kit }, kitAnswers: true);
        var t0 = new DateTime(2026, 1, 1, 18, 0, 0);
        DateTime now = t0;
        b.Clock = () => now;
        FakeHost.UseDoneSeq = 10;
        RefuseKit(b, Kit); RefuseKit(b, Kit); RefuseKit(b, Kit);
        Check.True(VitalCast(b, "Heal Self"), "control: 3 refusals, the kit is no kit: Heal Self handled: " + Why(b));
        var c = Rig(CombatMode.Melee, new[] { Kit }, kitAnswers: true);
        DateTime cnow = t0;
        c.Clock = () => cnow;
        FakeHost.UseDoneSeq = 10;
        RefuseKit(c, Kit); RefuseKit(c, Kit); RefuseKit(c, Kit);
        cnow = t0.AddSeconds(11);
        Check.False(VitalCast(c, "Heal Self"), "11 s after the last refusal: the kit counts again, Heal Self refused");
        Check.True(Why(c).Contains("no wand swap for a vital spell", StringComparison.Ordinal), "the gate's reason: " + Why(c));
    }

    // No wand found (no wand rule, none carried): the Magic stance goes bare-handed and no weapon moves.
    private static void NoWandNotGated()
    {
        var b = Rig(CombatMode.Melee, new[] { Kit }, noWand: true);
        VitalCast(b, "Heal Self");
        Check.False(Why(b).Contains("no wand swap", StringComparison.Ordinal), "no wand: not refused by the gate: " + Why(b));
        Check.Eq(FakeHost.CombatModeRequests.Count(m => m == CombatMode.Magic), 1, "the bare-handed Magic stance request goes out");
        Check.Eq(FakeHost.Wields.Count, 0, "no wield");
    }

    private static void SetVitals(BuffManager b, uint health, uint stamina, uint mana)
    {
        var v = (PlayerVitalsCache)typeof(BuffManager).GetField("_vitals", Np)!.GetValue(b)!;
        v.CurrentHealth = health; v.CurrentStamina = stamina; v.CurrentMana = mana;
    }

    private static void StaminaKitDespiteMinSuccess()
    {
        foreach (int minSuccess in new[] { 95, 0 })
        {
            var b = Rig(CombatMode.Melee, new[] { StamKit }, kitMinSuccess: minSuccess);
            S.RestamAt = 50;
            SetVitals(b, 100, 20, 100);
            Check.True(b.CheckVitals(), $"min {minSuccess}: stamina 20% under Re-stam At 50%: the chain acts");
            Check.Eq(FakeHost.UsesOn.Count, 1, $"min {minSuccess}: one kit use");
            Check.Eq(FakeHost.UsesOn[0].Source, StamKit, $"min {minSuccess}: the stamina kit");
            bool lastResort = FakeHost.Logs.Any(l => l.Contains("using the stamina kit despite Stamina Kit Min Success", StringComparison.Ordinal));
            if (minSuccess == 95) Check.True(lastResort, "under Kit Min Success: used as the last resort, logged");
            else Check.False(lastResort, "control, chance fine: the normal kit use, no last-resort line");
            Check.Eq(FakeHost.CombatModeRequests.Count(m => m == CombatMode.Magic), 0, $"min {minSuccess}: no Magic stance");
            Check.Eq(FakeHost.Casts.Count, 0, $"min {minSuccess}: no Revitalize");
        }
    }

    private static void ManaInFightNoSwap()
    {
        // No mana potion, no kit: only the fight decides.
        var f = Rig(CombatMode.Melee, Array.Empty<uint>(), usePotions: true);
        f.InCombatOverride = () => true;
        Check.False(VitalCast(f, "Stamina to Mana Self"), "in a fight: Stamina to Mana not handled");
        Check.True(Why(f).Contains("Stamina to Mana in a fight", StringComparison.Ordinal), "the why says so: " + Why(f));
        Check.Eq(SwapActivity(), 0, "in a fight: no swap");

        var q = Rig(CombatMode.Melee, Array.Empty<uint>(), usePotions: true);
        q.InCombatOverride = () => false;
        int before = SwapActivity();
        Check.True(VitalCast(q, "Stamina to Mana Self"), "out of a fight, no mana potion: handled (the swap step): " + Why(q));
        Check.True(SwapActivity() > before, "out of a fight: the wand swap started");

        // A caster in hand needs no swap: Stamina to Mana still goes in a fight.
        var c = Rig(CombatMode.Melee, Array.Empty<uint>(), wandWielded: true);
        c.InCombatOverride = () => true;
        VitalCast(c, "Stamina to Mana Self");
        Check.False(Why(c).Contains("no wand swap", StringComparison.Ordinal), "caster in hand, in a fight: not gated: " + Why(c));
    }

    private static LegacyUiSettings S = new();

    private static BuffManager Rig(int mode, uint[] items, bool wandWielded = false, bool usePotions = false, int kitMinSuccess = 0, bool kitAnswers = false,
        bool walk = false, bool noWand = false)
    {
        FakeHost.Reset();
        uint[] carried = (noWand ? Array.Empty<uint>() : new[] { Wand }).Concat(items).ToArray();
        if (walk)
        {
            FakeHost.ContentsCalls = true;           // the client's pack walk (GetContainerContents)
            foreach (uint id in carried) FakeHost.Containers[id] = Player;
        }
        FakeHost.WeaponCalls = true;
        FakeHost.CombatModeCalls = true;
        FakeHost.UseOnCalls = true;
        if (kitAnswers) { FakeHost.ServerGates = true; FakeHost.LastUseDoneCalls = true; }   // a kit's UseDone answers
        FakeHost.CombatModeValue = mode;
        foreach (var row in SpellTable.Rows) FakeHost.Spellbook.Add((uint)row.Id);
        FakeHost.Skills[(Player, 33)] = (400, 2);   // Life Magic
        FakeHost.Skills[(Player, 21)] = (10, 2);    // Healing, low: a kit chance under any real Kit Min Success
        if (!noWand)
        {
            FakeHost.Names[Wand] = "Wand";
            FakeHost.ItemTypes[Wand] = 0x8000;      // a caster
            FakeHost.Appraised.Add(Wand);           // identified: FindWandInItems takes it (a real swap, not bare-handed Magic)
        }
        FakeHost.Names[Kit] = "Treated Healing Kit";
        // Lesser (boost 100): (10 + 100) x 1.1 = 121 vs 80 missing x 2 x 1.1 = 176 in melee, 16%, under the
        // weapon-out bar of 30 (a Greater kit's boost 200 clears it).
        FakeHost.Names[StamKit] = "Lesser Stamina Kit";
        FakeHost.Names[ManaPot] = "Mana Elixir";
        FakeHost.Names[HealPot] = "Healing Elixir";
        if (wandWielded) FakeHost.Ints[(Wand, CurrentWieldedLocation)] = unchecked((int)HeldSlot);
        var host = FakeHost.Create(Player);
        S = new LegacyUiSettings
        {
            IsMacroRunning = true, HealAt = 75, TopOffHP = 75, EmergencyHealAt = 0, StaminaToHealthAt = 0,
            GetManaAt = 0, TopOffMana = 0, RestamAt = 0, TopOffStam = 0, UsePotions = usePotions, EnableBuffing = false,
            KitMinSuccessPct = kitMinSuccess, StaminaKitMinSuccessPct = kitMinSuccess, PeaceModeForKits = false,
        };
        if (!noWand) S.ItemRules.Add(new ItemRule { Id = unchecked((int)Wand), Name = "Wand", Element = "Fire" });
        if (items.Contains(ManaPot)) S.ConsumableRules.Add(new ConsumableRule { Id = unchecked((int)ManaPot), Name = "Mana Elixir", Type = "ManaPotion" });
        if (items.Contains(HealPot)) S.ConsumableRules.Add(new ConsumableRule { Id = unchecked((int)HealPot), Name = "Healing Elixir", Type = "HealthPotion" });
        var skills = new CharacterSkills(host);
        skills.SetPlayerId(Player);
        var sm = new SpellManager(host, S);
        sm.InitializeNatively();
        sm.SetCharacterSkills(skills);
        var v = new PlayerVitalsCache { MaxHealth = 100, CurrentHealth = 40, MaxStamina = 100, CurrentStamina = 100, MaxMana = 100, CurrentMana = 100 };
        var b = new BuffManager(host, S, sm, v);
        b.SetCharacterSkills(skills);
        b.MarkLiveBuffsReadyForTests();
        b.InCombatOverride = () => true;
        b.SetWorldObjectCache(FakeHost.MakeCache(host, Player, carried));
        DateTime now = new DateTime(2026, 1, 1, 18, 0, 0);
        b.Clock = () => now;
        return b;
    }

    private static readonly BindingFlags Np = BindingFlags.Instance | BindingFlags.NonPublic;

    private static bool VitalCast(BuffManager b, string baseName)
        => (bool)typeof(BuffManager).GetMethod("AttemptVitalCast", Np)!.Invoke(b, new object[] { baseName })!;

    private static string Why(BuffManager b) => (string)typeof(BuffManager).GetField("_vitalWhy", Np)!.GetValue(b)!;

    /// <summary>Everything a wand swap or stance change sends or logs.</summary>
    private static int SwapActivity() => FakeHost.CombatModeRequests.Count + FakeHost.Wields.Count + FakeHost.Uses.Count
        + FakeHost.Logs.Count(l => l.Contains("[WieldGate] switching to", StringComparison.Ordinal));

    private static void HealRefusedWithKit()
    {
        foreach (int mode in new[] { CombatMode.Melee, CombatMode.Missile, CombatMode.NonCombat })
        {
            var b = Rig(mode, new[] { Kit });
            int before = SwapActivity();
            Check.False(VitalCast(b, "Heal Self"), $"mode {mode}, kit carried: Heal Self not handled");
            Check.True(Why(b).Contains("no wand swap for a vital spell", StringComparison.Ordinal), $"mode {mode}: the why says so: {Why(b)}");
            Check.Eq(SwapActivity(), before, $"mode {mode}: no stance change, wield or swap step");
            Check.Eq(FakeHost.Casts.Count, 0, $"mode {mode}: nothing cast");
        }
        // A health potion alone (no kit) refuses it too.
        var p = Rig(CombatMode.Melee, new[] { HealPot }, usePotions: true);
        Check.False(VitalCast(p, "Stamina to Health Self"), "health potion carried: Stamina to Health not handled");
        Check.Eq(SwapActivity(), 0, "and no swap");
    }

    private static void KitDespiteMinSuccessAfterRefusal()
    {
        var b = Rig(CombatMode.Melee, new[] { Kit }, kitMinSuccess: 95);
        Check.True(b.CheckVitals(), "40% under Heal At 75%: the chain acts");
        Check.Eq(FakeHost.UsesOn.Count, 1, "one kit use");
        Check.Eq(FakeHost.UsesOn[0].Source, Kit, "the kit Kit Min Success skipped");
        Check.True(FakeHost.Logs.Any(l => l.Contains("despite Kit Min Success", StringComparison.Ordinal)), "the last resort is logged");
        Check.Eq(FakeHost.CombatModeRequests.Count(m => m == CombatMode.Magic), 0, "no Magic stance asked for");
        Check.Eq(FakeHost.Casts.Count, 0, "no Heal Self");
    }

    private static void StanceOnlyWithCasterWielded()
    {
        var b = Rig(CombatMode.Melee, new[] { Kit }, wandWielded: true);
        Check.False(Why(b).Contains("no wand swap", StringComparison.Ordinal), "rig");
        VitalCast(b, "Heal Self");
        Check.False(Why(b).Contains("no wand swap for a vital spell", StringComparison.Ordinal), "caster in hand: not refused by the gate: " + Why(b));
        Check.Eq(FakeHost.CombatModeRequests.Count(m => m == CombatMode.Magic), 1, "the Magic stance change goes out");
        Check.Eq(FakeHost.Wields.Count, 0, "no wield");
        Check.Eq(FakeHost.Uses.Count, 0, "no use (no re-equip)");
        Check.False(FakeHost.Logs.Any(l => l.Contains("[WieldGate] switching to", StringComparison.Ordinal)), "no swap started");
    }

    private static void SwapAllowedWithNothingElse()
    {
        // Control for HealRefusedWithKit: the same rig with nothing else to heal with.
        var b = Rig(CombatMode.Melee, Array.Empty<uint>(), usePotions: true);
        int before = SwapActivity();
        bool handled = VitalCast(b, "Heal Self");
        Check.True(handled, "no kit, no potion: Heal Self handled (its swap step in flight): " + Why(b));
        Check.True(SwapActivity() > before, "the wand swap started");
        // A potion only, with Use Potions off: nothing RynthAi may use, so the swap is allowed.
        var k = Rig(CombatMode.Melee, new[] { HealPot }, usePotions: false);
        Check.True(VitalCast(k, "Heal Self"), "a potion Use Potions may not drink is no heal: the swap is allowed");
    }

    private static void OtherVitalsRefused()
    {
        var s = Rig(CombatMode.Melee, new[] { StamKit });
        Check.False(VitalCast(s, "Revitalize Self"), "stamina kit carried: Revitalize not handled");
        Check.Eq(SwapActivity(), 0, "no swap for Revitalize");
        var sc = Rig(CombatMode.Melee, new[] { Kit });
        Check.True(VitalCast(sc, "Revitalize Self"), "control: a healing kit is no stamina restore: Revitalize may swap");

        var m = Rig(CombatMode.Melee, new[] { ManaPot }, usePotions: true);
        m.InCombatOverride = () => true;   // the potion alone no longer refuses it (out of a fight it may swap): the fight does
        Check.False(VitalCast(m, "Stamina to Mana Self"), "in a fight, mana potion carried: Stamina to Mana not handled");
        Check.Eq(SwapActivity(), 0, "no swap for Stamina to Mana in a fight");

        // The script hook's heal goes through CastVital with the Healing label.
        var h = Rig(CombatMode.Melee, new[] { HealPot }, usePotions: true);
        int heal = SpellTable.Rows.First(r => r.Name.StartsWith("Heal Self", StringComparison.Ordinal)).Id;
        object[] args = { heal, Player, BuffManager.LabelHealing, "Heal Self (script)", false };
        bool handled = (bool)typeof(BuffManager).GetMethod("CastVital", Np)!.Invoke(h, args)!;
        Check.False(handled, "script heal, health potion carried: not handled");
        Check.False((bool)args[4], "not sent");
        Check.Eq(SwapActivity(), 0, "no swap for the script heal");
    }

    private static void PureCasterUnaffected()
    {
        var b = Rig(CombatMode.Magic, new[] { Kit }, wandWielded: true);
        S.UseKitsInMagicMode = false;   // the kit is skipped in Magic: the spell is the heal
        Check.True(b.CheckVitals(), "a caster in Magic under Heal At: acts");
        Check.True(FakeHost.Casts.Count > 0, "Heal Self is cast: " + Why(b));
        Check.Eq(FakeHost.UsesOn.Count, 0, "no kit");
        Check.Eq(FakeHost.Wields.Count, 0, "no wield");
    }

    private static void BuffsStillSwap()
    {
        var b = Rig(CombatMode.Melee, new[] { Kit, HealPot }, usePotions: true);
        int before = SwapActivity();
        var ensure = typeof(BuffManager).GetMethod("EnsureMagicMode", Np)!;
        bool inMagic = (bool)ensure.Invoke(b, new object[] { true, false })!;
        Check.False(inMagic, "not in Magic yet");
        Check.True(SwapActivity() > before, "buffing starts the wand swap with a kit and potion carried");
    }
}

using System;
using System.Linq;
using System.Reflection;
using RynthCore.Plugin.RynthAi;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.RynthAiHostTests.Fakes;
using RynthCore.RynthAiTests.Harness;

namespace RynthCore.RynthAiHostTests.Tests;

// The mana reserve (2026-10-11). A void mage, Life Magic 304, at 1/383 mana and 289/383 stamina
// against Rampagers: "You don't have enough Mana to cast this spell", and RynthAi kept casting
// Corruption instead of Stamina to Mana. Combat had drained mana under what Stamina to Mana costs,
// its refusal parked it 15 s, and with no mana potion nothing held the arbiter for vitals. Now a
// caster keeps ManaReserveFactor x Stamina to Mana's cost: under it Stamina to Mana goes first and
// combat holds its casts; under the cost, the highest level it can pay for goes, or mana
// regenerates while attacks wait, and a mana refusal rests the vital only briefly.
internal static class ManaReserveTests
{
    private const uint Player = 0x50000B21;
    private const uint STypeLife = 33;
    private const uint STypeVoid = 43;
    private const int Target = 0x7000A021;
    private const string NotEnoughMana = "You don't have enough Mana to cast this spell.";

    public static void Register(Runner r)
    {
        r.Add("mana reserve: under 1.5x Stamina to Mana's cost, Stamina to Mana is chosen over an attack", ReserveChoosesStaminaToMana);
        r.Add("mana reserve: under Stamina to Mana's cost, no attack spam and no 15 s park on 'not enough Mana'", StarvedNoSpamNoPark);
        r.Add("mana reserve: as mana regenerates, the level it can pay for goes, then attacks resume", RecoversAsManaRegenerates);
        r.Add("mana reserve: stamina too low for Stamina to Mana (under Re-stam At, at least 15%) - stamina is refilled first", StaminaFirst);
        r.Add("mana reserve: combat holds an attack it can't pay for, and casts once mana is back", CombatHoldsCast);
        r.Add("mana reserve: a melee fighter's attacks aren't held; Get Mana At off means no reserve", MeleeAndOff);
        r.Add("mana reserve: the dat's BaseMana is read (when this PC has a portal.dat)", DatBaseMana);
        r.Add("mana reserve: a buff that would leave mana under the reserve waits for Stamina to Mana (melee too)", BuffWaitsForReserve);
        r.Add("mana reserve: Stamina to Mana can't go yet (stamina low): the buff waits instead of spending the reserve, up to a minute", BuffWaitsWhenStaminaToManaCantGo);
        r.Add("mana reading: unchanged over three buff casts, buffs wait for it to move", StuckManaReadingHoldsBuffs);
    }

    private static int Family(string name) => SpellTableStub.GetById(SpellTable.Named(name).First().Id)!.Family;
    private static int Id(string name) => SpellTable.Named(name).First().Id;
    private static string NameOf(int id) => SpellTableStub.GetById(id)?.Name ?? $"#{id}";
    private static int CostByLevel(int id) => (SpellTableStub.GetById(id)?.Level ?? 0) * 10;
    private static int FamilyOfCast(int i) => SpellTableStub.GetById(FakeHost.Casts[i].SpellId)?.Family ?? 0;

    private sealed class Rig
    {
        public readonly BuffManager B;
        public readonly LegacyUiSettings S;
        public readonly PlayerVitalsCache V;
        public bool InCombat = true;

        public Rig(uint mana, uint stamina = 289, int getManaAt = 20, int combatMode = 0)
        {
            FakeHost.Reset();
            FakeHost.WeaponCalls = true;
            FakeHost.CombatModeValue = combatMode == 0 ? CombatMode.Magic : combatMode;
            foreach (var row in SpellTable.Rows) FakeHost.Spellbook.Add((uint)row.Id);
            FakeHost.Skills[(Player, STypeLife)] = (304, 2);
            FakeHost.Skills[(Player, STypeVoid)] = (400, 3);
            BuffManager.ManaCostOverride = CostByLevel;   // level x 10: I 10 ... VII 70, VIII 80
            var host = FakeHost.Create();
            S = new LegacyUiSettings
            {
                IsMacroRunning = true, HealAt = 0, TopOffHP = 0, EmergencyHealAt = 0, StaminaToHealthAt = 0,
                GetManaAt = getManaAt, TopOffMana = getManaAt, RestamAt = 0, TopOffStam = 0, UsePotions = false,
                EnableBuffing = false,
            };
            var skills = new CharacterSkills(host);
            skills.SetPlayerId(Player);
            var sm = new SpellManager(host, S);
            sm.InitializeNatively();
            sm.SetCharacterSkills(skills);
            V = new PlayerVitalsCache { MaxHealth = 400, CurrentHealth = 400, MaxStamina = 383, CurrentStamina = stamina, MaxMana = 383, CurrentMana = mana };
            B = new BuffManager(host, S, sm, V);
            B.SetCharacterSkills(skills);
            B.InCombatOverride = () => InCombat;
        }

        public int LogCount(string part) => FakeHost.Logs.Count(l => l.Contains(part, StringComparison.Ordinal));
    }

    private static BotActivity Arbiter(BuffManager b)
    {
        var inputs = new ArbiterInputs(true, b.PendingSpellId != 0 || b.WantsVitalRecharge, wantCombat: true,
                                       wantLooting: false, wantSalvaging: false, wantNav: true, combatEngaged: true);
        return ActivityArbiter.Decide(in inputs);
    }

    // 2026-10-12, Drakkon (melee): a full rebuff spent mana down to 6%, under Stamina to Mana's cost.
    private static void BuffWaitsForReserve()
    {
        var r = new Rig(mana: 300);   // in Magic stance: the buff path readies magic before this check
        int bane = Id("Blade Bane VI");                 // level VI: cost 60 here
        int reserve = r.B.ManaReserve();
        Check.True(reserve > 0, $"a reserve for a melee character too ({reserve})");

        Check.False(r.B.BuffWaitsForMana(bane, "Blade Bane"), "300/383: the buff goes");
        Check.Eq(FakeHost.Casts.Count, 0, "nothing cast by the check");

        r.V.CurrentMana = (uint)(reserve + 30);       // the buff (60) would leave it under the reserve
        Check.True(r.B.BuffWaitsForMana(bane, "Blade Bane"), "the buff waits");
        Check.Eq(FakeHost.Casts.Count, 1, "Stamina to Mana is cast instead");
        Check.Eq(FamilyOfCast(0), Family("Stamina to Mana Self I"), $"it is Stamina to Mana ({NameOf(FakeHost.Casts[0].SpellId)})");

        // Under even the lowest Stamina to Mana's cost (10) and the buff's: buffing waits, no cast.
        var low = new Rig(mana: 5);
        Check.True(low.B.BuffWaitsForMana(bane, "Blade Bane"), "5/383: buffing waits for regen");
        Check.Eq(FakeHost.Casts.Count, 0, "nothing the server would refuse is sent");
    }

    // 2026-10-15, Drakkon after a death: under the reserve with Stamina to Mana unable to go, buffs went
    // anyway, down to 5% - under Stamina to Mana's own cost.
    private static void BuffWaitsWhenStaminaToManaCantGo()
    {
        var r = new Rig(mana: 300, stamina: 10);   // 10/383 stamina: under Stamina to Mana's 15% line, no kit or potion
        int bane = Id("Blade Bane VI");
        r.V.CurrentMana = (uint)(r.B.ManaReserve() + 30);
        Check.True(r.B.BuffWaitsForMana(bane, "Blade Bane"), "the buff waits: it would spend the reserve");
        Check.Eq(FakeHost.Casts.Count, 0, "Stamina to Mana can't go, and the buff doesn't either");
        Check.True(r.B.BuffWaitsForMana(bane, "Blade Bane"), "still waiting");
        typeof(BuffManager).GetField("_reserveWaitSince", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(r.B, DateTime.Now.AddSeconds(-BuffManager.ReserveWaitMaxSec - 1));
        Check.False(r.B.BuffWaitsForMana(bane, "Blade Bane"), "after a minute the buff goes (no stall for good)");
        Check.True(r.LogCount("goes under the mana reserve") == 1, "and that is logged");
    }

    private static void StuckManaReadingHoldsBuffs()
    {
        var r = new Rig(mana: 300);
        r.B.NoteBuffCastMana(60);
        r.V.CurrentMana = 240;
        r.B.NoteBuffCastMana(60);
        Check.False(r.B.ManaReadingStuck(), "the reading moves with each cast");
        r.B.NoteBuffCastMana(60);
        Check.False(r.B.ManaReadingStuck(), "one cast at the same reading is not enough");
        r.B.NoteBuffCastMana(60);
        Check.True(r.B.ManaReadingStuck(), "three casts at 240: the reading is stale, buffs wait");
        Check.True(r.LogCount("has not moved") == 1, "logged once");
        r.V.CurrentMana = 60;
        Check.False(r.B.ManaReadingStuck(), "a fresh reading: buffs go on (and judge 60, not 240)");
        Check.False(r.B.ManaReadingStuck(), "and stay going");
    }

    private static void ReserveChoosesStaminaToMana()
    {
        // Over Get Mana At 20% (77 of 383), but under 1.5x the top Stamina to Mana's cost.
        var r = new Rig(mana: 300);
        int corruption = Id("Corruption VII");
        int reserve = r.B.ManaReserve();
        Check.True(reserve > 77, $"the reserve ({reserve}) is over Get Mana At's 77 points");
        r.V.CurrentMana = (uint)(reserve - 5);
        Check.True(r.V.ManaPct >= 20, $"{r.V.CurrentMana}/383 is over Get Mana At");
        Check.True(r.B.WantsManaNow(), "under the reserve, in combat: vitals want the tick");
        Check.True(r.B.WantsVitalRecharge, "...which is what the arbiter reads");
        Check.Eq(Arbiter(r.B), BotActivity.Buffing, "the arbiter gives vitals the tick over combat");
        string? hold = r.B.OffensiveCastHold(corruption);
        Check.True(hold != null && hold.Contains("reserve"), $"combat's Corruption VII waits ({hold})");

        Check.True(r.B.CheckVitals(), "vitals act");
        Check.Eq(FakeHost.Casts.Count, 1, "one cast");
        Check.Eq(FamilyOfCast(0), Family("Stamina to Mana Self I"), $"it is Stamina to Mana ({NameOf(FakeHost.Casts[0].SpellId)})");
        int cost = CostByLevel(FakeHost.Casts[0].SpellId);
        Check.Eq(r.B.ManaReserve(), (int)Math.Ceiling(cost * BuffManager.ManaReserveFactor), "the reserve is 1.5x that level's cost");

        // Plenty of mana: nothing held.
        r.V.CurrentMana = 300;
        Check.False(r.B.WantsManaNow(), "300/383: no hold");
        Check.Null(r.B.OffensiveCastHold(corruption), "300/383: combat casts");
    }

    private static void StarvedNoSpamNoPark()
    {
        // The report: 1/383 mana. No Stamina to Mana level costs 1 or less.
        var r = new Rig(mana: 1);
        int corruption = Id("Corruption VII");
        for (int i = 0; i < 5; i++)
            Check.True(r.B.CheckVitals(), $"check {i}: vitals hold the tick (mana regenerating)");
        Check.Eq(FakeHost.Casts.Count, 0, "no Stamina to Mana sent that the server would refuse");
        Check.Eq(r.LogCount("is under"), 1, "logged once");
        Check.True(r.LogCount("attacks wait, regenerating") == 1, "the log says attacks wait");
        Check.True(r.B.WantsManaNow(), "vitals keep the tick while it regenerates");
        Check.Eq(Arbiter(r.B), BotActivity.Buffing, "combat doesn't get it");
        string? hold = r.B.OffensiveCastHold(corruption);
        Check.True(hold != null && hold.Contains("Corruption VII's cost"), $"an attack under its own cost waits ({hold})");
        Check.Eq(r.B.BuffingLabel, BuffManager.LabelMana, "the slot reads Restoring mana");

        // Costs unknown (no dat): Stamina to Mana is tried; refused for mana it rests 2 s, not 15 s,
        // and mana regenerates meanwhile with attacks still held.
        var u = new Rig(mana: 50);
        BuffManager.ManaCostOverride = _ => 0;
        // Mana Conversion untrained: the rest below. Trained, a recovery vital retries in 200 ms
        // instead (RecoveryRetryTests).
        FakeHost.Skills[(Player, 16)] = (10, 1);
        Check.True(u.B.CheckVitals(), "50/383 under Get Mana At: Stamina to Mana goes");
        Check.Eq(FakeHost.Casts.Count, 1, "one cast");
        u.B.OnChatWindowText(NotEnoughMana, 0);
        Check.True(FakeHost.Logs.Any(l => l.Contains("not enough mana; resting the family 2s", StringComparison.Ordinal)), "rested 2 s, not 15 s");
        Check.True(u.B.CheckVitals(), "inside the rest: vitals still hold (regenerating)");
        Check.Eq(FakeHost.Casts.Count, 1, "no re-cast inside the rest");
        Check.True(u.B.WantsManaNow(), "the short rest doesn't let combat in");
        System.Threading.Thread.Sleep(2100);
        u.V.CurrentMana = 70;
        Check.True(u.B.CheckVitals(), "2 s later");
        Check.Eq(FakeHost.Casts.Count, 2, "Stamina to Mana is cast again");
        Check.Eq(FamilyOfCast(1), Family("Stamina to Mana Self I"), "Stamina to Mana");
    }

    private static void RecoversAsManaRegenerates()
    {
        var r = new Rig(mana: 1);
        Check.True(r.B.CheckVitals(), "1 mana: waiting");
        Check.Eq(FakeHost.Casts.Count, 0, "nothing cast");

        r.V.CurrentMana = 15;   // pays for level I (10), nothing higher
        Check.True(r.B.CheckVitals(), "15 mana");
        Check.Eq(FakeHost.Casts.Count, 1, "a Stamina to Mana goes");
        Check.True(CostByLevel(FakeHost.Casts[0].SpellId) <= 15, $"the level it can pay for ({NameOf(FakeHost.Casts[0].SpellId)})");

        r.V.CurrentMana = (uint)(r.B.ManaReserve() - 1);   // the top level is payable again; still under the reserve
        Check.True(r.B.CheckVitals(), "just under the reserve");
        Check.Eq(FakeHost.Casts.Count, 2, "Stamina to Mana again");
        int top = r.B.ManaReserve();
        Check.True(CostByLevel(FakeHost.Casts[1].SpellId) * BuffManager.ManaReserveFactor >= top - 1,
                   $"the top level this time ({NameOf(FakeHost.Casts[1].SpellId)})");

        r.V.CurrentMana = 300;
        Check.False(r.B.WantsManaNow(), "300 mana: vitals let go");
        Check.Null(r.B.OffensiveCastHold(Id("Corruption VII")), "attacks resume");
        Check.False(r.B.CheckVitals(), "nothing for vitals to do");
    }

    private static void StaminaFirst()
    {
        // 50 mana (under Get Mana At 20% = 77) and 10% stamina: Stamina to Mana can't go (stamina
        // under 15%), so Revitalize first - at a level 50 mana pays for.
        var r = new Rig(mana: 50, stamina: 38);
        Check.True(r.B.CheckVitals(), "vitals act");
        Check.Eq(FakeHost.Casts.Count, 1, "one cast");
        Check.Eq(FamilyOfCast(0), Family("Revitalize Self I"), $"Revitalize ({NameOf(FakeHost.Casts[0].SpellId)})");
        Check.True(CostByLevel(FakeHost.Casts[0].SpellId) <= 50, "a level the mana pays for");
        Check.Eq(r.B.BuffingLabel, BuffManager.LabelStamina, "Restoring stamina");
        Check.False(r.B.WantsManaNow(), "with no stamina kit or potion, attacks aren't held for mana (Revitalize costs mana too)");

        // Re-stam At off and Stamina to Mana unaffordable doesn't matter: stamina goes first all the same.
        var off = new Rig(mana: 50, stamina: 38);
        off.S.RestamAt = 0;
        Check.True(off.B.CheckVitals(), "Re-stam At 0: still refilled for Stamina to Mana");
        Check.Eq(FamilyOfCast(0), Family("Revitalize Self I"), "Revitalize");

        // 2026-10-14: the floor is Re-stam At (when over 15): at 25% stamina with Re-stam At 30,
        // stamina is refilled before Stamina to Mana (each one cost Drakkon 110-126 stamina).
        var floor = new Rig(mana: 50, stamina: 96);   // 25%
        floor.S.RestamAt = 30;
        Check.Eq(floor.B.StaminaToManaFloorPct(), 30, "the floor is Re-stam At");
        Check.True(floor.B.CheckVitals(), "vitals act");
        Check.Eq(FamilyOfCast(0), Family("Revitalize Self I"), $"25% under Re-stam At 30: stamina first ({NameOf(FakeHost.Casts[0].SpellId)})");
        var over = new Rig(mana: 50, stamina: 134);   // 35%
        over.S.RestamAt = 30;
        Check.True(over.B.CheckVitals(), "vitals act");
        Check.Eq(FamilyOfCast(0), Family("Stamina to Mana Self I"), $"35%: Stamina to Mana ({NameOf(FakeHost.Casts[0].SpellId)})");
    }

    private static void CombatHoldsCast()
    {
        FakeHost.Reset();
        foreach (string n in new[] { "Flame Bolt VI" })
            foreach (var row in SpellTable.Named(n)) FakeHost.Spellbook.Add((uint)row.Id);
        var s = new LegacyUiSettings { IsMacroRunning = true, GetManaAt = 20, TopOffMana = 20, UsePotions = false, RingRange = 0 };
        s.MinSkillLevelTier1 = 0; s.MinSkillLevelTier2 = 0; s.MinSkillLevelTier3 = 0; s.MinSkillLevelTier4 = 0;
        s.MinSkillLevelTier5 = 0; s.MinSkillLevelTier6 = 0; s.MinSkillLevelTier7 = 10000; s.MinSkillLevelTier8 = 10000;
        s.MonsterRules.Add(new MonsterRule { Name = "Rampager", DamageType = "Fire", UseBolt = true });
        FakeHost.PlayerPose = (0x0101001Bu, 0f, 0f, 0f);
        FakeHost.Positions[Player] = FakeHost.PlayerPose;
        FakeHost.Positions[(uint)Target] = (0x0101001Bu, 0f, 10f, 0f);
        FakeHost.Names[(uint)Target] = "Rampager";
        FakeHost.CombatModeValue = CombatMode.Magic;
        BuffManager.ManaCostOverride = CostByLevel;
        var host = FakeHost.Create(Player);
        var cache = FakeHost.MakeCache(host, Player, new[] { (uint)Target });
        var c = new CombatManager(host, s, cache);
        c.SetPlayerId(Player);
        var sm = new SpellManager(host, s);
        sm.InitializeNatively();
        sm.RefreshKnownSpells();
        c.SetSpellManager(sm);
        var v = new PlayerVitalsCache { MaxHealth = 400, CurrentHealth = 400, MaxStamina = 383, CurrentStamina = 289, MaxMana = 383, CurrentMana = 1 };
        var b = new BuffManager(host, s, sm, v);
        b.InCombatOverride = () => true;
        c.ManaReserveHold = id => b.OffensiveCastHold(id);
        c.activeTargetId = Target;
        var target = new WorldObject(Target, "Rampager");
        MethodInfo attack = typeof(CombatManager).GetMethod("AttackWithMagic", BindingFlags.NonPublic | BindingFlags.Instance)!;
        FieldInfo held = typeof(CombatManager).GetField("_manaCastHeld", BindingFlags.NonPublic | BindingFlags.Instance)!;

        for (int i = 0; i < 3; i++) attack.Invoke(c, new object[] { target });
        Check.Eq(FakeHost.Casts.Count, 0, "1 mana: Flame Bolt VI (60) isn't sent");
        Check.True((bool)held.GetValue(c)!, "held for mana");
        Check.Eq(c.LastCombatSkipReason, "mana-reserve", "the skip reason says so");
        Check.Eq(FakeHost.Logs.Count(l => l.Contains("[CombatCast] attacks wait: mana 1/383 is under Flame Bolt VI's cost (60)", StringComparison.Ordinal)), 1, "logged once");

        v.CurrentMana = 300;
        attack.Invoke(c, new object[] { target });
        Check.Eq(FakeHost.Casts.Count, 1, "mana back: the bolt goes");
        Check.Eq(NameOf(FakeHost.Casts[0].SpellId), "Flame Bolt VI", "Flame Bolt VI");
        Check.False((bool)held.GetValue(c)!, "not held");
        Check.True(FakeHost.Logs.Any(l => l.Contains("attacks resume", StringComparison.Ordinal)), "and says attacks resume");
    }

    private static void MeleeAndOff()
    {
        // A melee fighter (no combat manager: the stance decides) at low mana: no hold of the fight.
        var melee = new Rig(mana: 1, combatMode: CombatMode.Melee);
        Check.False(melee.B.WantsManaNow(), "melee: vitals don't hold the fight for mana");
        // But the reserve is its refill line too (2026-10-14: a melee character's buffs spent mana under
        // Stamina to Mana's cost and nothing could refill it).
        melee.V.CurrentMana = 85;   // 22%: over Get Mana At 20%, under the reserve (90)
        Check.True(melee.B.ManaUnderLine(true, out int line), $"melee: under the reserve ({line})");
        Check.Eq(line, 90, "the line is the reserve");

        // Get Mana At 0 (mana recharge off in combat): no reserve, nothing held.
        var off = new Rig(mana: 100, getManaAt: 0);
        Check.False(off.B.WantsManaNow(), "Get Mana At 0: no hold");
        Check.Null(off.B.OffensiveCastHold(Id("Corruption VII")), "Get Mana At 0: combat casts (it can pay)");
        Check.False(off.B.CheckVitals(), "and vitals do nothing for mana");
    }

    private static void DatBaseMana()
    {
        var saved = BuffManager.ManaCostOverride;
        BuffManager.ManaCostOverride = null;
        try
        {
            int s2m1 = Id("Stamina to Mana Self I");
            if (!ComponentDatabase.TryGetSpellBaseMana(s2m1, out int m1))
            {
                Console.WriteLine("      (no portal.dat on this PC: BaseMana not checked)");
                return;
            }
            string[] names = { "Stamina to Mana Self I", "Stamina to Mana Self IV", "Stamina to Mana Self VI",
                               "Revitalize Self VI", "Heal Self VI", "Corruption VII", "Flame Bolt VI" };
            string line = string.Join(", ", names.Select(n => $"{n} {BuffManager.SpellManaCost(Id(n))}"));
            Console.WriteLine("      dat BaseMana: " + line);
            Check.True(m1 > 0, $"Stamina to Mana Self I costs something ({m1})");
            Check.True(BuffManager.SpellManaCost(Id("Stamina to Mana Self VI")) > m1, "level VI costs more than level I");
        }
        finally { BuffManager.ManaCostOverride = saved; }
    }
}

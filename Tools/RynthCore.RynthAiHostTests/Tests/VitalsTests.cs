using System.Linq;
using RynthCore.Loot;
using System.Text.Json;
using RynthCore.Plugin.RynthAi;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.PluginSdk;
using RynthCore.RynthAiHostTests.Fakes;
using RynthCore.RynthAiTests.Harness;

namespace RynthCore.RynthAiHostTests.Tests;

// Self vitals (BuffManager.CheckVitals): when Stamina to Health Self is cast. It has its own
// lines, Stamina To Health At and Stamina To Health Min Stamina, apart from Emergency Heal At
// (kits and potions). Run in Magic mode with no kits or potions and every other vital line at
// 0, so the only thing CheckVitals can do is the Stamina to Health cast.
internal static class VitalsTests
{
    private const uint Player = 0x50000B01;
    private const uint STypeLife = 33;

    public static void Register(Runner r)
    {
        r.Add("vitals: Stamina to Health follows its own health line and stamina floor", StaminaToHealthThresholds);
        r.Add("vitals: profiles without the Stamina to Health keys load today's 30 / 20", StaminaToHealthDefaults);
        r.Add("vitals: Re-stam To keeps a started recharge going to its line; off = one recharge as before", RestamTo);
        r.Add("vitals: a recharge started in a fight, or under Re-stam At just after one, finishes to Re-stam To", RestamRunOutlivesFight);
        r.Add("vitals: a Re-stam To run counts casts sent, not the ticks spent waiting for one", RestamRunCountsSentCasts);
        r.Add("vitals: out of Magic stance a listed stamina potion goes before Revitalize; a caster casts first", StaminaPotionFirst);
        r.Add("vitals: no wand swap for the between-fights stamina top-off; under Re-stam At, with a potion or in Magic it acts as before", NoWandSwapForStaminaTopOff);
        r.Add("vitals: a listed potion whose stack ran out is drunk from the next stack of that name", ListedPotionNextStack);
        r.Add("vitals: after a kit, nothing else heals until the kit's result line lands", KitResultWait);
        r.Add("vitals: Heal Self known only above the tier Life Magic allows (vitae) is still cast", HealAboveAllowedTier);
        r.Add("vitals: low health wins the arbiter even on a busy tick; backs off when nothing can heal", HealthWinsArbiter);
        r.Add("vitals: health falling with nothing in the scan uses the fight's Heal At, not Top Off HP, for 5 s", BeingHitIsAFight);
        r.Add("vitals: 'You're too busy!' paces vital casts, and a long streak stops the character once", TooBusyVitals);
        r.Add("vitals: no components for the top level - the next lower level is cast, not the same one again", NoComponentsFallsBack);
        // Drakkon's death, 2026-10-05 13:43:20 (ops\overnight\2026-10-05-death-fixes.md).
        r.Add("vitals: a hot reload's login refresh trusts its first read; a real login waits for two equal reads", HotReloadRefresh);
        r.Add("vitals: RynthAi tells the engine a reload is unsafe while engaged, under a heal line or casting", ReloadBlockerReport);
    }

    // Drakkon, 2026-10-05: stuck in the cast gesture, AC answering "You're too busy!" every
    // 0.4 s. The buff path backed off; Stamina to Mana didn't, and re-cast on every
    // SpellCastIntervalMs tick until the player moved. Mana/stamina casts now wait out the
    // backoff, and a streak of refusals sends one StopCompletely (what moving did).
    private static void TooBusyVitals()
    {
        FakeHost.Reset();
        FakeHost.WeaponCalls = true;
        FakeHost.StopCalls = true;
        FakeHost.CombatModeValue = CombatMode.Magic;
        foreach (var row in SpellTable.Rows) FakeHost.Spellbook.Add((uint)row.Id);
        FakeHost.Skills[(Player, STypeLife)] = (400, 2);
        var host = FakeHost.Create();
        var s = new LegacyUiSettings { IsMacroRunning = true, HealAt = 0, TopOffHP = 0, EmergencyHealAt = 0, StaminaToHealthAt = 0,
                                       GetManaAt = 50, TopOffMana = 50, RestamAt = 0, TopOffStam = 0, UsePotions = false };
        var skills = new CharacterSkills(host);
        skills.SetPlayerId(Player);
        var sm = new SpellManager(host, s);
        sm.InitializeNatively();
        sm.SetCharacterSkills(skills);
        var vitals = new PlayerVitalsCache { MaxHealth = 100, CurrentHealth = 100, MaxStamina = 100, CurrentStamina = 100, MaxMana = 100, CurrentMana = 10 };
        var b = new BuffManager(host, s, sm, vitals);
        b.SetCharacterSkills(skills);

        Check.True(b.CheckVitals(), "10% mana: Stamina to Mana is cast");
        Check.Eq(FakeHost.Casts.Count, 1, "one cast");
        b.OnChatWindowText("You're too busy!", 26);
        Check.True(b.CheckVitals(), "too busy: the vital tick still yields (no potion, no buff)");
        Check.Eq(FakeHost.Casts.Count, 1, "but nothing is re-cast inside the backoff");

        for (int i = 0; i < 3; i++) b.OnChatWindowText("You're too busy!", 26);
        Check.Eq(FakeHost.Stops, 1, "4 refusals in a row: one StopCompletely to clear the stuck gesture");
        for (int i = 0; i < 4; i++) b.OnChatWindowText("You're too busy!", 26);
        Check.Eq(FakeHost.Stops, 1, "only once per streak");
    }

    // 2026-10-08: with no components for the highest Stamina to Mana / Revitalize / Heal the
    // character knew, RynthAi kept retrying that one level; it parked the whole family, then
    // tried the same level again. Now that level is passed over and the next lower one is cast.
    private static void NoComponentsFallsBack()
    {
        FakeHost.Reset();
        FakeHost.WeaponCalls = true;
        FakeHost.CombatModeValue = CombatMode.Magic;
        foreach (var row in SpellTable.Rows) FakeHost.Spellbook.Add((uint)row.Id);
        FakeHost.Skills[(Player, STypeLife)] = (400, 2);
        var host = FakeHost.Create();
        var s = new LegacyUiSettings { IsMacroRunning = true, HealAt = 0, TopOffHP = 0, EmergencyHealAt = 0, StaminaToHealthAt = 0,
                                       GetManaAt = 50, TopOffMana = 50, RestamAt = 0, TopOffStam = 0, UsePotions = false };
        var skills = new CharacterSkills(host);
        skills.SetPlayerId(Player);
        var sm = new SpellManager(host, s);
        sm.InitializeNatively();
        sm.SetCharacterSkills(skills);
        var vitals = new PlayerVitalsCache { MaxHealth = 100, CurrentHealth = 100, MaxStamina = 100, CurrentStamina = 100, MaxMana = 100, CurrentMana = 10 };
        var b = new BuffManager(host, s, sm, vitals);
        b.SetCharacterSkills(skills);

        Check.True(b.CheckVitals(), "10% mana: Stamina to Mana is cast");
        int top = FakeHost.Casts[^1].SpellId;
        b.OnChatWindowText("You don't have all the components for this spell.", 0);
        for (int i = 0; i < 50 && FakeHost.Casts.Count < 2; i++)
        {
            b.CheckVitals();
            System.Threading.Thread.Sleep(20);
        }
        Check.Eq(FakeHost.Casts.Count, 2, "a second cast goes out");
        int next = FakeHost.Casts[^1].SpellId;
        Check.True(next != top, $"a different level ({SpellTableStub.GetById(top)?.Name} -> {SpellTableStub.GetById(next)?.Name})");
        Check.True(SpellTableStub.GetById(next)?.Family == SpellTableStub.GetById(top)?.Family, "the same spell, one level down");
    }

    // 2026-10-04, "nav and looting have priority over healing, almost dying in packs": the
    // arbiter only saw a heal on ticks where CheckVitals ran (never while looting kept the client
    // busy). Health under a line now says "wants vitals" by itself.
    // 2026-11-06: a mob behind a wall isn't in the scan, so a character it hit read as idle and every
    // hit put him under Top Off HP: kits for 45 s and nothing else ran.
    private static void BeingHitIsAFight()
    {
        FakeHost.Reset();
        FakeHost.WeaponCalls = true;
        foreach (var row in SpellTable.Rows) FakeHost.Spellbook.Add((uint)row.Id);
        FakeHost.Skills[(Player, STypeLife)] = (400, 2);
        var host = FakeHost.Create();
        var s = new LegacyUiSettings { IsMacroRunning = true, HealAt = 60, TopOffHP = 95, EmergencyHealAt = 0, StaminaToHealthAt = 0, UsePotions = false };
        var skills = new CharacterSkills(host);
        skills.SetPlayerId(Player);
        var sm = new SpellManager(host, s);
        sm.InitializeNatively();
        sm.SetCharacterSkills(skills);
        var v = new PlayerVitalsCache { MaxHealth = 100, CurrentHealth = 100, MaxStamina = 100, CurrentStamina = 100, MaxMana = 100, CurrentMana = 100 };
        var b = new BuffManager(host, s, sm, v);
        b.SetCharacterSkills(skills);

        Check.False(b.WantsVitalRecharge, "full health: nothing to do");
        v.CurrentHealth = 80;
        Check.False(b.WantsVitalRecharge, "hit down to 80%: a fight, and over Heal At 60");
        v.CurrentHealth = 50;
        Check.True(b.WantsVitalRecharge, "50% is under Heal At: heals");
    }

    private static void HealthWinsArbiter()
    {
        BuffManager Make(byte lifeTraining, out PlayerVitalsCache vitals, out LegacyUiSettings s)
        {
            FakeHost.Reset();
            FakeHost.WeaponCalls = true;
            FakeHost.CombatModeValue = CombatMode.Magic;
            foreach (var row in SpellTable.Rows) FakeHost.Spellbook.Add((uint)row.Id);
            FakeHost.Skills[(Player, STypeLife)] = (400, lifeTraining);
            var host = FakeHost.Create();
            s = new LegacyUiSettings { IsMacroRunning = true, HealAt = 60, TopOffHP = 60, EmergencyHealAt = 0, StaminaToHealthAt = 0, UsePotions = false };
            var skills = new CharacterSkills(host);
            skills.SetPlayerId(Player);
            var sm = new SpellManager(host, s);
            sm.InitializeNatively();
            sm.SetCharacterSkills(skills);
            vitals = new PlayerVitalsCache { MaxHealth = 100, CurrentHealth = 40, MaxStamina = 100, CurrentStamina = 100, MaxMana = 100, CurrentMana = 100 };
            var b = new BuffManager(host, s, sm, vitals);
            b.SetCharacterSkills(skills);
            return b;
        }

        var b = Make(2, out var v, out var st);
        Check.True(b.WantsVitalRecharge, "40% health under Heal At 60: wants vitals before CheckVitals ever ran (a busy tick)");
        v.CurrentHealth = 80;
        Check.False(b.WantsVitalRecharge, "80% health: no");
        v.CurrentHealth = 40;
        st.IsMacroRunning = false;
        Check.False(b.WantsVitalRecharge, "macro off: no");

        var none = Make(1, out _, out _);   // Life Magic untrained, no kits, no potions: nothing can heal
        Check.True(none.WantsVitalRecharge, "before any check it still asks");
        none.CheckVitals();
        Check.False(none.WantsVitalRecharge, "after a check that healed nothing: backs off, the bot isn't held");
    }

    // The double-kit bug (2026-10-03): the tick after a kit still read the old health and used
    // another. While a kit is pending, CheckVitals does nothing else (here: no Stamina to Health
    // at 30% health, which it would otherwise cast); the heal line plus a short grace frees it.
    private static void KitResultWait()
    {
        Check.True(CastsStamToHealth(30, 50), "baseline: Stamina to Health is cast at 30% health");

        FakeHost.Reset();
        FakeHost.WeaponCalls = true;
        FakeHost.CombatModeValue = CombatMode.Magic;
        foreach (var row in SpellTable.Rows) FakeHost.Spellbook.Add((uint)row.Id);
        FakeHost.Skills[(Player, STypeLife)] = (400, 2);
        var host = FakeHost.Create();
        var s = new LegacyUiSettings { HealAt = 0, TopOffHP = 0, RestamAt = 0, TopOffStam = 0, GetManaAt = 0, TopOffMana = 0, UsePotions = false };
        var skills = new CharacterSkills(host);
        skills.SetPlayerId(Player);
        var sm = new SpellManager(host, s);
        sm.InitializeNatively();
        sm.SetCharacterSkills(skills);
        var vitals = new PlayerVitalsCache { MaxHealth = 100, CurrentHealth = 30, MaxStamina = 100, CurrentStamina = 50, MaxMana = 100, CurrentMana = 100 };
        var b = new BuffManager(host, s, sm, vitals);
        b.SetCharacterSkills(skills);

        b.MarkKitUsed();
        Check.True(b.CheckVitals(), "kit pending: CheckVitals holds the vitals step");
        Check.Eq(FakeHost.Casts.Count, 0, "kit pending: no spell cast meanwhile");

        b.OnChatWindowText("Bob says, \"heal yourself\"", 0);
        System.Threading.Thread.Sleep(800);
        Check.True(b.KitPending(), "speech that says \"heal yourself\" doesn't end the wait");
        b.OnChatWindowText("You heal yourself for 52 Health points.", 0);
        Check.True(b.KitPending(), "result line seen: still waiting out the short grace for the vitals");
        System.Threading.Thread.Sleep(800);
        Check.False(b.KitPending(), "after the grace the kit is done");
        Check.True(b.CheckVitals() && FakeHost.Casts.Count > 0, "and the vitals step runs again");

        Check.True(BuffManager.IsKitResultLine("you fail to heal yourself."), "fail line ends the wait");
        Check.True(BuffManager.IsKitResultLine("your movement disrupted healing!"), "disrupted line ends the wait");
        Check.True(BuffManager.IsKitResultLine("lucy mcjuicy is already at full health!"), "full-health line ends the wait");
        Check.False(BuffManager.IsKitResultLine("bob heals you for 40 health points."), "someone else's kit doesn't");
    }

    private static void HealAboveAllowedTier()
    {
        FakeHost.Reset();
        FakeHost.WeaponCalls = true;
        FakeHost.CombatModeValue = CombatMode.Magic;
        int heal6 = SpellTable.Rows.First(r => r.Name == "Heal Self VI").Id;
        FakeHost.Spellbook.Add((uint)heal6);              // the only Heal Self known
        FakeHost.Skills[(Player, STypeLife)] = (120, 2);  // vitae: Life Magic under the tier 6 line
        var host = FakeHost.Create();
        var s = new LegacyUiSettings { HealAt = 60, TopOffHP = 60, RestamAt = 0, TopOffStam = 0, GetManaAt = 0, TopOffMana = 0, UsePotions = false };
        var skills = new CharacterSkills(host);
        skills.SetPlayerId(Player);
        var sm = new SpellManager(host, s);
        sm.InitializeNatively();
        sm.SetCharacterSkills(skills);
        Check.True(sm.GetHighestBuffSpellTier(AcSkillType.LifeMagic) < 6, "the rig: Life Magic 120 allows less than tier 6");
        var vitals = new PlayerVitalsCache { MaxHealth = 100, CurrentHealth = 30, MaxStamina = 100, CurrentStamina = 100, MaxMana = 100, CurrentMana = 100 };
        var b = new BuffManager(host, s, sm, vitals);
        b.SetCharacterSkills(skills);
        Check.True(b.CheckVitals(), "health 30% under Heal At 60%: the vitals step acts");
        Check.True(FakeHost.Casts.Any(c => c.SpellId == heal6), "Heal Self VI is cast: " + string.Join(",", FakeHost.Casts.Select(c => c.SpellId)));
    }

    /// <summary>One CheckVitals on a fresh BuffManager; true when it cast a Stamina to Health spell.</summary>
    private static bool CastsStamToHealth(int healthPct, int staminaPct, System.Action<LegacyUiSettings>? tweak = null)
    {
        FakeHost.Reset();
        FakeHost.WeaponCalls = true;                       // GetCurrentCombatMode
        FakeHost.CombatModeValue = CombatMode.Magic;       // no wand swap in the way
        foreach (var row in SpellTable.Rows) FakeHost.Spellbook.Add((uint)row.Id);
        FakeHost.Skills[(Player, STypeLife)] = (400, 2);
        var host = FakeHost.Create();

        var s = new LegacyUiSettings
        {
            HealAt = 0, TopOffHP = 0, RestamAt = 0, TopOffStam = 0, GetManaAt = 0, TopOffMana = 0,
            UsePotions = false,
        };
        tweak?.Invoke(s);

        var skills = new CharacterSkills(host);
        skills.SetPlayerId(Player);
        var sm = new SpellManager(host, s);
        sm.InitializeNatively();
        sm.SetCharacterSkills(skills);
        var vitals = new PlayerVitalsCache
        {
            MaxHealth = 100, CurrentHealth = (uint)healthPct,
            MaxStamina = 100, CurrentStamina = (uint)staminaPct,
            MaxMana = 100, CurrentMana = 100,
        };
        var b = new BuffManager(host, s, sm, vitals);
        b.SetCharacterSkills(skills);

        int stamToHealth = sm.GetDynamicSelfBuffId("Stamina to Health Self", AcSkillType.LifeMagic);
        Check.True(stamToHealth != 0, "a Stamina to Health spell is known at this skill");
        bool handled = b.CheckVitals();
        bool cast = FakeHost.Casts.Any(c => c.SpellId == stamToHealth);
        Check.Eq(handled, cast, $"hp {healthPct} stam {staminaPct}: CheckVitals reports handled exactly when it cast");
        return cast;
    }

    // Tom, 2026-10-06: a melee character should drink its listed stamina potions before Revitalize
    // (which means switching the weapon for the wand and back).
    private static void StaminaPotionFirst()
    {
        const uint Tincture = 0x80001234;
        foreach (int mode in new[] { CombatMode.Melee, CombatMode.Magic })
        {
            FakeHost.Reset();
            FakeHost.WeaponCalls = true;
            FakeHost.CombatModeValue = mode;
            foreach (var row in SpellTable.Rows) FakeHost.Spellbook.Add((uint)row.Id);
            FakeHost.Skills[(Player, STypeLife)] = (400, 2);
            FakeHost.Names[Tincture] = "Stamina Tincture";
            var host = FakeHost.Create();
            var s = new LegacyUiSettings { IsMacroRunning = true, HealAt = 0, TopOffHP = 0, EmergencyHealAt = 0, StaminaToHealthAt = 0,
                                           GetManaAt = 0, TopOffMana = 0, RestamAt = 30, TopOffStam = 95, UsePotions = true, EnableBuffing = false };
            s.ConsumableRules.Add(new ConsumableRule { Id = unchecked((int)Tincture), Name = "Stamina Tincture", Type = "Stamina" });
            var skills = new CharacterSkills(host);
            skills.SetPlayerId(Player);
            var sm = new SpellManager(host, s);
            sm.InitializeNatively();
            sm.SetCharacterSkills(skills);
            var vitals = new PlayerVitalsCache { MaxHealth = 100, CurrentHealth = 100, MaxStamina = 100, CurrentStamina = 20, MaxMana = 100, CurrentMana = 100 };
            var b = new BuffManager(host, s, sm, vitals);
            b.SetCharacterSkills(skills);
            b.SetWorldObjectCache(FakeHost.MakeCache(host, Player, new[] { Tincture }));
            Check.True(b.CheckVitals(), $"mode {mode}: stamina 20% acts");
            bool drank = FakeHost.Uses.Contains(Tincture);
            if (mode == CombatMode.Melee)
            {
                Check.True(drank, "melee: the tincture is drunk");
                Check.Eq(FakeHost.Casts.Count, 0, "melee: no Revitalize (no wand switch)");
            }
            else
            {
                Check.False(drank, "caster in Magic stance: no potion first");
                Check.True(FakeHost.Casts.Count > 0, "caster: Revitalize is cast");
            }
        }
    }

    // The owner, 2026-11-07: a melee character with no stamina kit or potion swapped weapons for the
    // wand and back (about 5.5 s) for every between-fights stamina top-off. Out of a fight and over
    // Re-stam At it now waits for regeneration; under Re-stam At, in Magic stance, or with a potion,
    // stamina is handled as before.
    private static void NoWandSwapForStaminaTopOff()
    {
        const uint Tincture = 0x80001234;
        BuffManager Rig(int mode, int stamina, bool potion, out PlayerVitalsCache vitals)
        {
            FakeHost.Reset();
            FakeHost.WeaponCalls = true;
            FakeHost.CombatModeValue = mode;
            foreach (var row in SpellTable.Rows) FakeHost.Spellbook.Add((uint)row.Id);
            FakeHost.Skills[(Player, STypeLife)] = (400, 2);
            if (potion) FakeHost.Names[Tincture] = "Stamina Tincture";
            var host = FakeHost.Create();
            var s = new LegacyUiSettings { IsMacroRunning = true, HealAt = 0, TopOffHP = 0, EmergencyHealAt = 0, StaminaToHealthAt = 0,
                                           GetManaAt = 0, TopOffMana = 0, RestamAt = 30, RestamTo = 90, TopOffStam = 60,
                                           UsePotions = potion, EnableBuffing = false };
            if (potion) s.ConsumableRules.Add(new ConsumableRule { Id = unchecked((int)Tincture), Name = "Stamina Tincture", Type = "Stamina" });
            var skills = new CharacterSkills(host);
            skills.SetPlayerId(Player);
            var sm = new SpellManager(host, s);
            sm.InitializeNatively();
            sm.SetCharacterSkills(skills);
            vitals = new PlayerVitalsCache { MaxHealth = 100, CurrentHealth = 100, MaxStamina = 100, CurrentStamina = (uint)stamina, MaxMana = 100, CurrentMana = 100 };
            var b = new BuffManager(host, s, sm, vitals) { InCombatOverride = () => false };
            b.SetCharacterSkills(skills);
            b.SetWorldObjectCache(FakeHost.MakeCache(host, Player, potion ? new[] { Tincture } : System.Array.Empty<uint>()));
            return b;
        }

        var melee = Rig(CombatMode.Melee, 50, potion: false, out _);
        Check.False(melee.CheckVitals(), "melee, idle, 50% under Top Off 60, nothing but the spell: nothing to do");
        Check.Eq(FakeHost.Casts.Count, 0, "no Revitalize");
        Check.False(FakeHost.Wields.Count > 0 || FakeHost.CombatModeRequests.Contains(CombatMode.Magic), "and no wand swap or Magic stance");
        Check.True(FakeHost.Logs.Any(l => l.Contains("no wand swap for it out of a fight")), "logged why");

        melee = Rig(CombatMode.Melee, 25, potion: false, out _);
        Check.True(melee.CheckVitals(), "melee, idle, 25% under Re-stam At 30: the recharge goes (wand swap and Revitalize)");

        var drink = Rig(CombatMode.Melee, 50, potion: true, out _);
        Check.True(drink.CheckVitals(), "melee, idle, 50% with a tincture: still topped off");
        Check.True(FakeHost.Uses.Contains(Tincture), "the tincture is drunk");

        var caster = Rig(CombatMode.Magic, 50, potion: false, out _);
        Check.True(caster.CheckVitals(), "in Magic stance (no swap needed): the top-off casts as before");
        Check.True(FakeHost.Casts.Count > 0, "Revitalize is cast");
    }

    // Report 2026-10-10: 200+ Stamina Tinctures, and RynthAi stopped drinking them whenever the stack
    // the rule was added from ran out, until the player added the next stack by hand.
    private static void ListedPotionNextStack()
    {
        const uint Listed = 0x80001234, NextStack = 0x80005678;
        FakeHost.Reset();
        FakeHost.WeaponCalls = true;
        FakeHost.CombatModeValue = CombatMode.Melee;
        foreach (var row in SpellTable.Rows) FakeHost.Spellbook.Add((uint)row.Id);
        FakeHost.Skills[(Player, STypeLife)] = (400, 2);
        FakeHost.Names[NextStack] = "Stamina Tincture";   // the listed stack (Listed) is gone
        var host = FakeHost.Create();
        var s = new LegacyUiSettings { IsMacroRunning = true, HealAt = 0, TopOffHP = 0, EmergencyHealAt = 0, StaminaToHealthAt = 0,
                                       GetManaAt = 0, TopOffMana = 0, RestamAt = 30, TopOffStam = 95, UsePotions = true, EnableBuffing = false };
        s.ConsumableRules.Add(new ConsumableRule { Id = unchecked((int)Listed), Name = "Stamina Tincture", Type = "Stamina" });
        var skills = new CharacterSkills(host);
        skills.SetPlayerId(Player);
        var sm = new SpellManager(host, s);
        sm.InitializeNatively();
        sm.SetCharacterSkills(skills);
        var vitals = new PlayerVitalsCache { MaxHealth = 100, CurrentHealth = 100, MaxStamina = 100, CurrentStamina = 20, MaxMana = 100, CurrentMana = 100 };
        var b = new BuffManager(host, s, sm, vitals);
        b.SetCharacterSkills(skills);
        b.SetWorldObjectCache(FakeHost.MakeCache(host, Player, new[] { NextStack }));
        Check.True(b.CheckVitals(), "stamina 20% acts");
        Check.True(FakeHost.Uses.Contains(NextStack), "the next stack of Stamina Tinctures is drunk");
        Check.False(FakeHost.Uses.Contains(Listed), "not the stack that ran out");
        Check.Eq(FakeHost.Casts.Count, 0, "no Revitalize while a tincture is carried");
    }

    // A recharge started in a fight finishes to Re-stam To when the fight ends, instead of dropping to
    // a lower Top Stam (Drakkon: Re-stam At 35, To 90, Top Stam 60).
    private static void RestamRunOutlivesFight()
    {
        var s = new LegacyUiSettings { RestamAt = 35, RestamTo = 90, TopOffStam = 60 };
        bool run = false; int casts = 0;
        Check.Eq(BuffManager.StaminaLine(true, 30, s, ref run, ref casts), 90, "fight, 30%: recharge to 90");
        casts++;
        Check.Eq(BuffManager.StaminaLine(false, 58, s, ref run, ref casts), 90, "fight over at 58%: the recharge still goes to 90");
        casts++;
        Check.Eq(BuffManager.StaminaLine(false, 91, s, ref run, ref casts), 60, "reached 90: done, the top-off line again");
        Check.False(run, "the recharge ended");
        Check.Eq(BuffManager.StaminaLine(false, 55, s, ref run, ref casts), 60, "no recharge running: idle uses Top Stam");

        // Drakkon, 10-23: the fight ended with stamina under Re-stam At before a recharge started (a
        // Stamina to Health drained 174 just before the kill): the top-off stopped at 60.
        run = false; casts = 0;
        Check.Eq(BuffManager.StaminaLine(false, 20, s, ref run, ref casts), 90, "no monster, 20%: under Re-stam At, recharge to 90");
        Check.True(run, "a recharge started");
        casts++;
        Check.Eq(BuffManager.StaminaLine(false, 65, s, ref run, ref casts), 90, "65% after one kit: still to 90, not Top Stam 60");
        casts++;
        Check.Eq(BuffManager.StaminaLine(false, 92, s, ref run, ref casts), 60, "reached 90: done");
        Check.Eq(BuffManager.StaminaLine(false, 40, s, ref run, ref casts), 60, "40% with no recharge running (over Re-stam At): Top Stam as before");
        var off = new LegacyUiSettings { RestamAt = 35, RestamTo = 0, TopOffStam = 60 };
        run = false; casts = 0;
        Check.Eq(BuffManager.StaminaLine(false, 20, off, ref run, ref casts), 60, "Re-stam To off: Top Stam as before");
        Check.False(run, "and no recharge run");
    }

    // Drakkon, 2026-10-20: every Re-stam To run stopped after one Robustification. The stamina chain also
    // answers "handled" while it waits for the last cast to finish, and each such tick counted as a cast:
    // the 4-cast cap was reached during the first cast's 2.3 s. Only casts actually sent count now.
    private static void RestamRunCountsSentCasts()
    {
        FakeHost.Reset();
        FakeHost.WeaponCalls = true;
        FakeHost.ServerGates = true;
        FakeHost.LastUseDoneCalls = true;
        FakeHost.CombatModeValue = CombatMode.Magic;
        foreach (var row in SpellTable.Rows) FakeHost.Spellbook.Add((uint)row.Id);
        FakeHost.Skills[(Player, STypeLife)] = (400, 2);
        var host = FakeHost.Create();
        var s = new LegacyUiSettings { IsMacroRunning = true, HealAt = 0, TopOffHP = 0, EmergencyHealAt = 0, StaminaToHealthAt = 0,
                                       GetManaAt = 0, TopOffMana = 0, RestamAt = 35, RestamTo = 90, TopOffStam = 60, UsePotions = false, EnableBuffing = false,
                                       SpellCastIntervalMs = 0 };
        var skills = new CharacterSkills(host);
        skills.SetPlayerId(Player);
        var sm = new SpellManager(host, s);
        sm.InitializeNatively();
        sm.SetCharacterSkills(skills);
        var vitals = new PlayerVitalsCache { MaxHealth = 100, CurrentHealth = 100, MaxStamina = 100, CurrentStamina = 30, MaxMana = 100, CurrentMana = 100 };
        var b = new BuffManager(host, s, sm, vitals) { InCombatOverride = () => true };
        b.SetCharacterSkills(skills);

        Check.True(b.CheckVitals(), "30% in a fight: the recharge starts");
        Check.Eq(FakeHost.Casts.Count, 1, "one Revitalize sent");
        for (int i = 0; i < 12; i++) b.CheckVitals();   // its 2.3 s: the server hasn't answered yet
        Check.Eq(FakeHost.Casts.Count, 1, "nothing re-cast while the first is out");

        string castName = SpellTableStub.GetById(FakeHost.Casts[0].SpellId)?.Name ?? "Revitalize Self";
        b.OnChatWindowText($"You cast {castName} and restore 25 points of your stamina.", 7);   // the cast lands: +25
        FakeHost.UseDone(0);
        vitals.CurrentStamina = 55;
        System.Threading.Thread.Sleep(600);              // real-time gaps between vital actions
        Check.True(b.CheckVitals(), "55%: under Re-stam To, the run goes on");
        Check.Eq(FakeHost.Casts.Count, 2, "a second Revitalize (the waits didn't use up the run)");
    }

    // Tom, 2026-10-06: one Revitalize often doesn't recover enough stamina.
    private static void RestamTo()
    {
        var s = new LegacyUiSettings { RestamAt = 30, RestamTo = 0, TopOffStam = 95 };
        bool run = false; int casts = 0;
        Check.Eq(BuffManager.StaminaLine(true, 25, s, ref run, ref casts), 30, "off: Re-stam At");
        Check.Eq(BuffManager.StaminaLine(true, 40, s, ref run, ref casts), 30, "off: at 40% no recharge");

        s.RestamTo = 70;
        Check.Eq(BuffManager.StaminaLine(true, 40, s, ref run, ref casts), 30, "above Re-stam At with no recharge started: nothing new");
        Check.Eq(BuffManager.StaminaLine(true, 25, s, ref run, ref casts), 70, "under Re-stam At: recharge to 70");
        casts++;
        Check.Eq(BuffManager.StaminaLine(true, 45, s, ref run, ref casts), 70, "one cast got it to 45%: still recharging (the second cast)");
        casts++;
        Check.Eq(BuffManager.StaminaLine(true, 72, s, ref run, ref casts), 30, "reached 70: done");
        Check.False(run, "the recharge ended");

        // A recharge that never gains stops after the cap.
        Check.Eq(BuffManager.StaminaLine(true, 20, s, ref run, ref casts), 70, "new recharge");
        casts = BuffManager.RestamRunMaxCasts;
        Check.Eq(BuffManager.StaminaLine(true, 35, s, ref run, ref casts), 30, "cap reached above Re-stam At: back to Re-stam At");

        // Out of combat the top-off line applies, as before.
        Check.Eq(BuffManager.StaminaLine(false, 50, s, ref run, ref casts), 95, "idle: Top Stam");
        s.RestamTo = 20;
        Check.Eq(BuffManager.StaminaLine(true, 25, s, ref run, ref casts), 30, "Re-stam To not above Re-stam At: ignored");
    }

    private static void StaminaToHealthThresholds()
    {
        // Defaults: today's behaviour (30% health, stamina over 20%).
        Check.True(CastsStamToHealth(30, 50), "defaults: cast at 30% health");
        Check.False(CastsStamToHealth(31, 50), "defaults: not above 30% health");
        Check.False(CastsStamToHealth(30, 20), "defaults: not at 20% stamina (must be over it)");
        Check.True(CastsStamToHealth(30, 21), "defaults: cast at 21% stamina");

        // Stamina To Health At is its own line.
        Check.False(CastsStamToHealth(10, 80, s => s.StaminaToHealthAt = 0), "Stamina To Health At 0: never cast");
        Check.True(CastsStamToHealth(45, 80, s => { s.StaminaToHealthAt = 50; s.EmergencyHealAt = 0; }),
            "Stamina To Health At 50 casts at 45% even with Emergency Heal At off");
        Check.False(CastsStamToHealth(45, 80, s => { s.StaminaToHealthAt = 30; s.EmergencyHealAt = 60; }),
            "Emergency Heal At 60 doesn't cast Stamina to Health above its own 30% line");

        // Stamina To Health Min Stamina.
        Check.False(CastsStamToHealth(20, 40, s => s.StaminaToHealthMinStamina = 50), "min stamina 50: not at 40%");
        Check.True(CastsStamToHealth(20, 60, s => s.StaminaToHealthMinStamina = 50), "min stamina 50: cast at 60%");
        Check.True(CastsStamToHealth(20, 5, s => s.StaminaToHealthMinStamina = 0), "min stamina 0: cast at 5%");

        // The pure predicate the emergency step uses.
        var d = new LegacyUiSettings();
        Check.True(BuffManager.StaminaToHealthAllowed(d, 30, 21), "predicate: 30 / 21 on defaults");
        Check.False(BuffManager.StaminaToHealthAllowed(d, 31, 99), "predicate: 31 on defaults");
    }

    private static BuffManager RefreshRig(out LegacyUiSettings s, out PlayerVitalsCache vitals, int healthPct = 100)
    {
        FakeHost.Reset();
        FakeHost.WeaponCalls = true;
        FakeHost.CombatModeValue = CombatMode.Magic;
        foreach (var row in SpellTable.Rows) FakeHost.Spellbook.Add((uint)row.Id);
        FakeHost.Skills[(Player, STypeLife)] = (400, 2);
        var host = FakeHost.Create();
        s = new LegacyUiSettings { IsMacroRunning = true, HealAt = 60, TopOffHP = 60, EmergencyHealAt = 30, StaminaToHealthAt = 0,
                                   GetManaAt = 0, TopOffMana = 0, RestamAt = 0, TopOffStam = 0, UsePotions = false, EnableBuffing = false };
        var skills = new CharacterSkills(host);
        skills.SetPlayerId(Player);
        var sm = new SpellManager(host, s);
        sm.InitializeNatively();
        sm.SetCharacterSkills(skills);
        vitals = new PlayerVitalsCache { MaxHealth = 100, CurrentHealth = (uint)healthPct, MaxStamina = 100, CurrentStamina = 100, MaxMana = 100, CurrentMana = 100 };
        var b = new BuffManager(host, s, sm, vitals);
        b.SetCharacterSkills(skills);
        return b;
    }

    // The engine reload at 13:42:55 looked like a login to RynthAi: the buff refresh waited 5.8 s
    // for two equal reads of a registry that was already complete (the client never left the world).
    private static void HotReloadRefresh()
    {
        int strength = SpellTable.Named("Strength Self I").First().Id;
        int endurance = SpellTable.Named("Endurance Self I").First().Id;

        var b = RefreshRig(out _, out _);
        FakeHost.ServerTime = 1000;
        FakeHost.Enchantments.Add(((uint)strength, 1000 + 3600));
        FakeHost.Enchantments.Add(((uint)endurance, 1000 + 3600));
        b.CarriedOverSession = true;
        b.OnHeartbeat();
        Check.True(b.LiveBuffsReady, "hot reload: ready on the first successful read");
        Check.True(FakeHost.Logs.Any(l => l.Contains("login refresh ready") && l.Contains("hot reload")), "and the line says why");

        var real = RefreshRig(out _, out _);
        FakeHost.ServerTime = 1000;
        FakeHost.Enchantments.Add(((uint)strength, 1000 + 3600));
        real.OnHeartbeat();
        Check.False(real.LiveBuffsReady, "a real login: one read isn't trusted (the registry may still be streaming)");
        System.Threading.Thread.Sleep(1100);
        real.OnHeartbeat();
        Check.True(real.LiveBuffsReady, "two equal reads a second apart: ready");
    }

    private static void ReloadBlockerReport()
    {
        // The pure text.
        Check.Eq(ReloadSafety.Reason(false, true, "Emergency Heal At", 12, 30, "Heal Self V"), "", "macro off: never blocks a reload");
        Check.Eq(ReloadSafety.Reason(true, false, "", 100, 0, ""), "", "nothing going on: safe");
        Check.Eq(ReloadSafety.Reason(true, true, "", 100, 0, ""), "in combat (a monster engaged)", "a monster engaged");
        Check.Eq(ReloadSafety.Reason(true, false, "Emergency Heal At", 12, 30, ""), "in combat (health 12% under Emergency Heal At 30%)", "under a heal line");
        Check.Eq(ReloadSafety.Reason(true, false, "", 100, 0, "Strength Self VI"), "casting ('Strength Self VI' awaiting its result)", "a buff in flight");
        Check.Eq(ReloadSafety.Reason(true, true, "Heal At", 45, 60, "Heal Self V"),
                 "in combat (a monster engaged, health 45% under Heal At 60%, 'Heal Self V' awaiting its result)", "all three");

        // What BuffManager feeds it.
        var b = RefreshRig(out _, out var v, healthPct: 50);
        b.MarkLiveBuffsReadyForTests();
        Check.Eq(b.HealLineNow(out int hp, out int lineValue), "Top Off HP", "50% under Top Off HP (no monster: the idle line)");
        Check.Eq((hp, lineValue), (50, 60), "with the health and the line");
        Check.Eq(b.CastInFlightName, "", "nothing cast yet");
        Check.True(b.CheckVitals(), "Heal Self is cast");
        int healCat = SpellTable.Named("Heal Self I").First().Category;
        Check.True(b.CastInFlightName.Length > 0 && SpellTable.Named(b.CastInFlightName).Any(row => row.Category == healCat),
                   $"the cast in flight is a Heal Self tier: {b.CastInFlightName}");
        v.CurrentHealth = 100;
        Check.Eq(b.HealLineNow(out _, out _), "", "full health: no line");
        v.MaxHealth = 0;
        Check.Eq(b.HealLineNow(out _, out _), "", "vitals not read yet: no line");
    }

    private static void StaminaToHealthDefaults()
    {
        var old = JsonSerializer.Deserialize("{\"EmergencyHealAt\":25}", RynthAiJsonContext.Default.LegacyUiSettings);
        Check.True(old != null, "an old profile parses");
        Check.Eq(old!.EmergencyHealAt, 25, "Emergency Heal At kept");
        Check.Eq(old.StaminaToHealthAt, 30, "Stamina To Health At defaults to 30");
        Check.Eq(old.StaminaToHealthMinStamina, 20, "Stamina To Health Min Stamina defaults to 20");

        var bridge = JsonSerializer.Deserialize("{\"EmergencyHealAt\":25}", RynthAiJsonContext.Default.SettingsBridgePayload);
        Check.Eq(bridge!.StaminaToHealthAt, 30, "settings bridge: Stamina To Health At defaults to 30");
        Check.Eq(bridge.StaminaToHealthMinStamina, 20, "settings bridge: Stamina To Health Min Stamina defaults to 20");
    }
}

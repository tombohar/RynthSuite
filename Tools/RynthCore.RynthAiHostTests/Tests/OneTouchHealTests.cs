using System;
using System.Linq;
using System.Reflection;
using System.Threading;
using RynthCore.Loot;
using RynthCore.Plugin.RynthAi;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.Plugin.Shared;
using RynthCore.PluginCore;
using RynthCore.PluginSdk;
using RynthCore.RynthAiHostTests.Fakes;
using RynthCore.RynthAiTests.Harness;

namespace RynthCore.RynthAiHostTests.Tests;

// One-touch heal / stamina / mana (BuffManager.OneTouch.cs; /ra heal, /ra stam, /ra mana, and RynthCore's
// "One-touch" hotkeys): one request uses the best kit, spell or potion for that vital now, whatever
// the Heal At / Top Off lines say, with the macro off. The choice is the vitals step's own
// (AttemptHealChain, AttemptStaminaChain, AttemptManaRefill), so these tests check the order and the
// waiting, not the individual attempts (VitalsTests, RecoveryRetryTests, ...).
internal static class OneTouchHealTests
{
    private const uint Player = 0x50000C01;
    private const uint Kit = 0x80004C01, StamKit = 0x80004C02, HealPotion = 0x80004C03, StamPotion = 0x80004C04;
    private const uint STypeLife = 33;
    private const BindingFlags Any = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

    public static void Register(Runner r)
    {
        r.Add("one-touch: a vital that is full says so and sends nothing", FullSaysSo);
        r.Add("one-touch: heal ignores the lines and casts Heal Self in Magic stance, macro off", HealCastsInMagic);
        r.Add("one-touch: heal with a weapon out takes the kit, then the potion, before the spell (no wand swap)", HealItemsBeforeSpellInMelee);
        r.Add("one-touch: heal in Magic stance keeps the vitals step's order: Heal Self before the potion", HealSpellBeforePotionInMagic);
        r.Add("one-touch: heal falls to Stamina to Health only when that feature is on and stamina is over its floor", HealStaminaToHealthLast);
        r.Add("one-touch: with nothing to heal with it says why for the kit, the spell and the potion", HealNothingSaysWhy);
        r.Add("one-touch: a request waits out a kit in flight, then goes; a second press while waiting is not a second heal", HealWaitsForKit);
        r.Add("one-touch: a request that stays blocked gives up after OneTouchWaitMs and says why", HealGivesUp);
        r.Add("one-touch: stamina takes a stamina kit, a listed potion out of Magic stance, else Revitalize", StaminaChoice);
        r.Add("vitals: a stamina kit is typed StaminaKit and never taken by the potion step (it was drunk with no target)", StaminaKitNeverDrunk);
        r.Add("buff wand: a caster that needs untrained War Magic is never picked to buff; the next listed one is", BuffWandSkipsUnwieldable);
        r.Add("one-touch: mana casts Stamina to Mana; with stamina at the floor it refills stamina first", ManaChoice);
        r.Add("one-touch: /ra heal, /ra stam and /ra mana request a pass (and say so before login)", Commands);
    }

    private sealed class Rig
    {
        public BuffManager B = null!;
        public LegacyUiSettings S = null!;
        public PlayerVitalsCache V = null!;
        public RynthCoreHost Host;
    }

    /// <summary>A character with every spell known, Life Magic trained, the macro off and every line at 0 (so no
    /// vitals step would act): the one-touch request is the only thing that can.</summary>
    private static Rig Make(int mode, uint hp = 40, uint st = 100, uint mn = 100, bool spells = true, bool kit = false,
        bool stamKit = false, bool healPotion = false, bool stamPotion = false, byte lifeTraining = 2, Action<LegacyUiSettings>? tweak = null)
    {
        FakeHost.Reset();
        FakeHost.WeaponCalls = true;
        FakeHost.UseOnCalls = true;
        FakeHost.CombatModeValue = mode;
        if (spells) foreach (var row in SpellTable.Rows) FakeHost.Spellbook.Add((uint)row.Id);
        FakeHost.Skills[(Player, STypeLife)] = (400, lifeTraining);
        var ids = new System.Collections.Generic.List<uint>();
        if (kit) { FakeHost.Names[Kit] = "Peerless Healing Kit"; ids.Add(Kit); }
        if (stamKit) { FakeHost.Names[StamKit] = "Greater Stamina Kit"; ids.Add(StamKit); }
        if (healPotion) { FakeHost.Names[HealPotion] = "Health Tincture"; ids.Add(HealPotion); }
        if (stamPotion) { FakeHost.Names[StamPotion] = "Stamina Tincture"; ids.Add(StamPotion); }
        var host = FakeHost.Create(Player);
        var s = new LegacyUiSettings
        {
            IsMacroRunning = false, HealAt = 0, TopOffHP = 0, EmergencyHealAt = 0, StaminaToHealthAt = 0,
            RestamAt = 0, TopOffStam = 0, GetManaAt = 0, TopOffMana = 0, UsePotions = healPotion || stamPotion,
            KitMinSuccessPct = 0, PeaceModeForKits = false, UseKitsInMagicMode = true, EnableBuffing = false,
        };
        if (healPotion) s.ConsumableRules.Add(new ConsumableRule { Id = unchecked((int)HealPotion), Name = "Health Tincture", Type = "HealthPotion" });
        if (stamPotion) s.ConsumableRules.Add(new ConsumableRule { Id = unchecked((int)StamPotion), Name = "Stamina Tincture", Type = "Stamina" });
        tweak?.Invoke(s);
        var skills = new CharacterSkills(host);
        skills.SetPlayerId(Player);
        var sm = new SpellManager(host, s);
        sm.InitializeNatively();
        sm.SetCharacterSkills(skills);
        var v = new PlayerVitalsCache { MaxHealth = 100, CurrentHealth = hp, MaxStamina = 100, CurrentStamina = st, MaxMana = 100, CurrentMana = mn };
        var b = new BuffManager(host, s, sm, v);
        b.SetCharacterSkills(skills);
        b.SetWorldObjectCache(FakeHost.MakeCache(host, Player, ids));
        ActionPacer.Reset();
        return new Rig { B = b, S = s, V = v, Host = host };
    }

    /// <summary>Heartbeats until the request is taken (or 400 ms): what the pump does after the command.</summary>
    private static void Pump(BuffManager b, OneTouchVital v)
    {
        for (int i = 0; i < 40 && b.OneTouchPending(v); i++) { b.OnHeartbeat(); Thread.Sleep(10); }
    }

    private static bool Said(string part) => FakeHost.Chat.Any(c => c.Contains(part, StringComparison.Ordinal));
    private static string AllChat => string.Join(" | ", FakeHost.Chat);

    /// <summary>The cast spell is a level of the family of <paramref name="levelOne"/> (top levels have names of their own).</summary>
    private static bool IsFamily(int spellId, string levelOne) =>
        SpellTableStub.GetById(spellId)?.Family == SpellTableStub.GetById(SpellTable.Rows.First(r => r.Name == levelOne).Id)?.Family;

    private static void FullSaysSo()
    {
        var r = Make(CombatMode.Magic, hp: 100, st: 100, mn: 100);
        foreach (var v in new[] { OneTouchVital.Health, OneTouchVital.Stamina, OneTouchVital.Mana })
        {
            r.B.RequestOneTouch(v);
            Check.True(r.B.OneTouchPending(v), v + ": pending until a heartbeat");
            Pump(r.B, v);
            Check.False(r.B.OneTouchPending(v), v + ": taken");
        }
        Check.Eq(FakeHost.Casts.Count, 0, "nothing cast");
        Check.Eq(FakeHost.UsesOn.Count + FakeHost.Uses.Count, 0, "nothing used");
        Check.True(Said("Heal: health is full") && Said("Stamina: stamina is full") && Said("Mana: mana is full"), AllChat);
    }

    private static void HealCastsInMagic()
    {
        // 95% health: over Heal At and Top Off HP (both 0 here, and 90 in a real setup): no vitals step would act.
        var r = Make(CombatMode.Magic, hp: 95);
        Check.False(r.B.CheckVitals(), "the normal step does nothing at 95%");
        r.B.RequestOneTouch(OneTouchVital.Health);
        Pump(r.B, OneTouchVital.Health);
        Check.Eq(FakeHost.Casts.Count, 1, "one cast");
        Check.True(IsFamily(FakeHost.Casts[0].SpellId, "Heal Self I"), "a Heal Self (the top level known)");
        Check.True(Said("Heal: casting "), AllChat);
        Check.False(r.S.IsMacroRunning, "the macro stayed off");
    }

    private static void HealItemsBeforeSpellInMelee()
    {
        // Melee, kit and potion and every spell: the kit goes, no cast (the wand swap is seconds).
        var r = Make(CombatMode.Melee, hp: 60, kit: true, healPotion: true);
        r.B.RequestOneTouch(OneTouchVital.Health);
        Pump(r.B, OneTouchVital.Health);
        Check.True(FakeHost.UsesOn.Any(u => u.Source == Kit && u.Target == Player), "the kit is used on yourself");
        Check.Eq(FakeHost.Casts.Count, 0, "no cast");
        Check.False(FakeHost.Uses.Contains(HealPotion), "and no potion on top of it");
        Check.True(Said("Heal: using a healing kit"), AllChat);

        // No kit: the potion, still before the spell.
        r = Make(CombatMode.Melee, hp: 60, healPotion: true);
        r.B.RequestOneTouch(OneTouchVital.Health);
        Pump(r.B, OneTouchVital.Health);
        Check.True(FakeHost.Uses.Contains(HealPotion), "melee, no kit: the potion is drunk");
        Check.Eq(FakeHost.Casts.Count, 0, "melee: no Heal Self (a wand swap) while a potion can do it");
        Check.True(Said("Heal: drinking a potion"), AllChat);

        // Neither: the spell (the wand swap / Magic stance steps are the vitals step's).
        r = Make(CombatMode.Melee, hp: 60);
        r.B.RequestOneTouch(OneTouchVital.Health);
        for (int i = 0; i < 40 && r.B.OneTouchPending(OneTouchVital.Health) && FakeHost.Casts.Count == 0; i++) { r.B.OnHeartbeat(); Thread.Sleep(10); }
        // Either the cast went out or a stance step is in flight (still pending); never a "nothing could heal".
        Check.False(Said("nothing could heal"), "a spell is possible: " + AllChat);
    }

    private static void HealSpellBeforePotionInMagic()
    {
        var r = Make(CombatMode.Magic, hp: 60, healPotion: true);
        r.B.RequestOneTouch(OneTouchVital.Health);
        Pump(r.B, OneTouchVital.Health);
        Check.Eq(FakeHost.Casts.Count, 1, "Magic stance: the spell, as the vitals step does");
        Check.False(FakeHost.Uses.Contains(HealPotion), "the potion is the fallback, not the first choice");

        // A kit still goes before the spell when kits are allowed in Magic mode (the step's own order) ...
        r = Make(CombatMode.Magic, hp: 60, kit: true);
        r.B.RequestOneTouch(OneTouchVital.Health);
        Pump(r.B, OneTouchVital.Health);
        Check.True(FakeHost.UsesOn.Any(u => u.Source == Kit), "Use Kits in Magic Mode on: the kit first");
        Check.Eq(FakeHost.Casts.Count, 0, "and no cast on top of it");

        // ... and not when they are not.
        r = Make(CombatMode.Magic, hp: 60, kit: true, tweak: s => s.UseKitsInMagicMode = false);
        r.B.RequestOneTouch(OneTouchVital.Health);
        Pump(r.B, OneTouchVital.Health);
        Check.Eq(FakeHost.UsesOn.Count, 0, "Use Kits in Magic Mode off: no kit");
        Check.Eq(FakeHost.Casts.Count, 1, "the spell instead");
    }

    private static void HealStaminaToHealthLast()
    {
        // Only Stamina to Health Self is known: used when the feature is on and stamina is over its floor.
        static Rig OnlyStamToHealth(uint stamina, int at)
        {
            var r = Make(CombatMode.Magic, hp: 70, st: stamina, spells: false, tweak: s => { s.StaminaToHealthAt = at; s.StaminaToHealthMinStamina = 20; });
            foreach (var row in SpellTable.Rows.Where(x => x.Name.StartsWith("Stamina to Health Self"))) FakeHost.Spellbook.Add((uint)row.Id);
            return r;
        }

        var r = OnlyStamToHealth(80, 30);
        r.B.RequestOneTouch(OneTouchVital.Health);
        Pump(r.B, OneTouchVital.Health);
        Check.Eq(FakeHost.Casts.Count, 1, "Stamina to Health Self is cast");
        Check.True(IsFamily(FakeHost.Casts[0].SpellId, "Stamina to Health Self I"), "Stamina to Health Self");

        r = OnlyStamToHealth(10, 30);
        r.B.RequestOneTouch(OneTouchVital.Health);
        Pump(r.B, OneTouchVital.Health);
        Check.Eq(FakeHost.Casts.Count, 0, "stamina under its floor: not cast");
        Check.True(Said("stamina 10% is not over 20%"), AllChat);

        r = OnlyStamToHealth(80, 0);
        r.B.RequestOneTouch(OneTouchVital.Health);
        Pump(r.B, OneTouchVital.Health);
        Check.Eq(FakeHost.Casts.Count, 0, "Stamina To Health At 0 (off): not cast");
        Check.True(Said("Stamina to Health: not on"), AllChat);
    }

    private static void HealNothingSaysWhy()
    {
        var r = Make(CombatMode.Magic, hp: 40, lifeTraining: 1);   // Life Magic untrained
        r.B.RequestOneTouch(OneTouchVital.Health);
        Pump(r.B, OneTouchVital.Health);
        Check.Eq(FakeHost.Casts.Count + FakeHost.UsesOn.Count + FakeHost.Uses.Count, 0, "nothing sent");
        Check.True(Said("Heal: nothing could heal (health 40%"), AllChat);
        Check.True(Said("kit: no healing kit") && Said("Heal Self: Life Magic not usable") && Said("potion: Use Potions is off"), AllChat);
        Check.False(r.B.OneTouchPending(OneTouchVital.Health), "and the request is over");
    }

    private static void HealWaitsForKit()
    {
        var r = Make(CombatMode.Magic, hp: 40);
        r.B.MarkKitUsed();   // a kit went out a moment ago and its result hasn't come
        r.B.RequestOneTouch(OneTouchVital.Health);
        r.B.RequestOneTouch(OneTouchVital.Health);   // pressed again: still one request
        for (int i = 0; i < 10; i++) { r.B.OnHeartbeat(); Thread.Sleep(10); }
        Check.True(r.B.OneTouchPending(OneTouchVital.Health), "waiting for the kit");
        Check.Eq(FakeHost.Casts.Count, 0, "nothing cast meanwhile");

        r.B.OnChatWindowText("You heal yourself for 30 Health points.", 0);
        Thread.Sleep(800);   // the vitals' grace after the result line
        Pump(r.B, OneTouchVital.Health);
        Check.False(r.B.OneTouchPending(OneTouchVital.Health), "taken once the kit is done");
        Check.Eq(FakeHost.Casts.Count, 1, "one heal, not two");
        Check.Eq(FakeHost.Chat.Count(c => c.StartsWith("[RynthAi] Heal:")), 1, "said once: " + AllChat);
    }

    private static void HealGivesUp()
    {
        var r = Make(CombatMode.Magic, hp: 40);
        r.B.MarkKitUsed();
        r.B.RequestOneTouch(OneTouchVital.Health);
        // Make the request old, as if it had been waiting OneTouchWaitMs.
        var at = (long[])typeof(BuffManager).GetField("_oneTouchAt", Any)!.GetValue(r.B)!;
        at[(int)OneTouchVital.Health] = Environment.TickCount64 - BuffManager.OneTouchWaitMs - 100;
        r.B.OnHeartbeat();
        Check.False(r.B.OneTouchPending(OneTouchVital.Health), "given up");
        Check.True(Said("Heal: not done, the character stayed busy (a kit is in flight)"), AllChat);
        Check.Eq(FakeHost.Casts.Count, 0, "nothing cast");
    }

    private static void StaminaChoice()
    {
        // A stamina kit first.
        var r = Make(CombatMode.Magic, st: 40, stamKit: true);
        r.B.RequestOneTouch(OneTouchVital.Stamina);
        Pump(r.B, OneTouchVital.Stamina);
        Check.True(FakeHost.UsesOn.Any(u => u.Source == StamKit && u.Target == Player), "the stamina kit is used");
        Check.Eq(FakeHost.Casts.Count, 0, "no Revitalize");
        Check.True(Said("Stamina: using a stamina kit"), AllChat);

        // Melee with a listed potion and no kit: the potion, not Revitalize (a wand swap).
        r = Make(CombatMode.Melee, st: 40, stamPotion: true);
        r.B.RequestOneTouch(OneTouchVital.Stamina);
        Pump(r.B, OneTouchVital.Stamina);
        Check.True(FakeHost.Uses.Contains(StamPotion), "melee: the stamina potion");
        Check.Eq(FakeHost.Casts.Count, 0, "melee: no Revitalize");

        // Magic stance, nothing else: Revitalize.
        r = Make(CombatMode.Magic, st: 40);
        r.B.RequestOneTouch(OneTouchVital.Stamina);
        Pump(r.B, OneTouchVital.Stamina);
        Check.Eq(FakeHost.Casts.Count, 1, "Revitalize Self");
        Check.True(IsFamily(FakeHost.Casts[0].SpellId, "Revitalize Self I"), "Revitalize Self");
    }

    // 2026-10-17: Drakkon's Greater Stamina Kit sat on the Items list as type "Stamina" (the old
    // name for stamina potions). The kit failed Kit Min Success, and the potion step then used it
    // with no target every 3 s; the server never answered and Revitalize never went out.
    private static void StaminaKitNeverDrunk()
    {
        var kit = new WorldObject(0x60002001, "Greater Stamina Kit", AcObjectClass.HealingKit);
        Check.Eq(RynthCore.Plugin.RynthAi.LegacyUi.LegacyWeaponsUi.PackConsumableType(kit), "StaminaKit",
            "a stamina kit is its own type: not a health kit, not a stamina potion");
        Check.Eq(RynthCore.Plugin.RynthAi.LegacyUi.LegacyWeaponsUi.PackConsumableType(new WorldObject(0x60002002, "Peerless Healing Kit", AcObjectClass.HealingKit)),
            "HealthKit", "a healing kit stays a health kit");
        Check.True(BuffManager.IsKitName("Greater Stamina Kit"), "a kit by name: the potion step skips it");
        Check.False(BuffManager.IsKitName("Stamina Tincture"), "a potion is not a kit");
    }

    // 2026-10-18 01:00-01:24: with the buff wand gone, buffing tried an Electric Baton that needs War
    // Magic (untrained) every 8 s for 24 minutes; Heal Self couldn't go and Drakkon died.
    private static void BuffWandSkipsUnwieldable()
    {
        var r = Make(CombatMode.Melee);
        const uint Baton = 0x80400070, Sceptre = 0x80400071;
        FakeHost.Ints[(Baton, 158)] = 2;      // WieldRequirements: RawSkill
        FakeHost.Ints[(Baton, 159)] = 34;     // War Magic
        FakeHost.Ints[(Baton, 160)] = 300;
        FakeHost.Skills[(Player, 34)] = (0, 1);   // War Magic untrained
        FakeHost.Appraised.Add(Baton); FakeHost.Appraised.Add(Sceptre);   // both identified: their requirements are known
        var skip = typeof(BuffManager).GetMethod("SkipBuffWand", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var baton = new WorldObject(unchecked((int)Baton), "Electric Baton", AcObjectClass.WandStaffOrb);
        var sceptre = new WorldObject(unchecked((int)Sceptre), "Sceptre", AcObjectClass.WandStaffOrb);
        Check.True((bool)skip.Invoke(r.B, new object[] { baton })!, "the War Magic baton is skipped");
        Check.False((bool)skip.Invoke(r.B, new object[] { sceptre })!, "a caster with no requirement is not");
        Check.True(FakeHost.Chat.Any(c => c.Contains("Electric Baton can't be wielded")), "the player is told once: " + AllChat);
        // Not identified yet: skipped, and identified first (the Acid Staff was tried unidentified, 2026-10-18).
        var staff = new WorldObject(0x60400073, "Acid Staff", AcObjectClass.WandStaffOrb);
        Check.True((bool)skip.Invoke(r.B, new object[] { staff })!, "an unidentified caster is not tried");
        Check.True(FakeHost.IdRequests.Contains(0x60400073u), "it is identified first");

        var rules = new[] { new ItemRule { Id = baton.Id, Name = baton.Name }, new ItemRule { Id = sceptre.Id, Name = sceptre.Name } };
        var objs = new[] { baton, sceptre };
        int pick = WeaponList.FindWand(rules, id => objs.FirstOrDefault(o => o.Id == id), _ => false, objs, false, out _,
            wo => wo.Id == baton.Id);
        Check.Eq(pick, sceptre.Id, "the finder passes over the skipped baton to the next listed caster");

        // The player wielded an unlisted caster (the one he can wield): that is the buff wand.
        var bronze = new WorldObject(0x60400072, "Bronze Sceptre", AcObjectClass.WandStaffOrb);
        var withBronze = new[] { baton, sceptre, bronze };
        int inHand = WeaponList.FindWand(rules, id => withBronze.FirstOrDefault(o => o.Id == id), wo => wo.Id == bronze.Id, withBronze, false, out _);
        Check.Eq(inHand, bronze.Id, "a caster already in hand wins, listed or not");
    }

    private static void ManaChoice()
    {
        var r = Make(CombatMode.Magic, st: 100, mn: 20);
        r.B.RequestOneTouch(OneTouchVital.Mana);
        Pump(r.B, OneTouchVital.Mana);
        Check.Eq(FakeHost.Casts.Count, 1, "one cast");
        Check.True(IsFamily(FakeHost.Casts[0].SpellId, "Stamina to Mana Self I"), "Stamina to Mana Self");
        Check.True(Said("Mana: casting "), AllChat);

        // Stamina at the floor: a stamina kit first, so Stamina to Mana has something to spend.
        r = Make(CombatMode.Magic, st: 10, mn: 20, stamKit: true);
        r.B.RequestOneTouch(OneTouchVital.Mana);
        Pump(r.B, OneTouchVital.Mana);
        Check.True(FakeHost.UsesOn.Any(u => u.Source == StamKit), "stamina kit first");
        Check.Eq(FakeHost.Casts.Count, 0, "no Stamina to Mana yet");
        Check.True(Said("stamina first, for Stamina to Mana"), AllChat);
    }

    private static void Commands()
    {
        var r = Make(CombatMode.Magic, hp: 40);
        var p = new RynthAiPlugin();
        typeof(RynthPluginBase).GetMethod("Attach", Any)!.Invoke(p, new object[] { FakeHost.LastApi });
        typeof(RynthAiPlugin).GetField("_buffManager", Any)!.SetValue(p, r.B);
        void Ra(string line)
        {
            string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            typeof(RynthAiPlugin).GetMethod("DispatchRaCommand", Any)!.Invoke(p, new object[] { parts[1].ToLowerInvariant(), parts, 0 });
        }

        foreach (var (line, vital) in new[]
        {
            ("/ra heal", OneTouchVital.Health), ("/ra stam", OneTouchVital.Stamina), ("/ra mana", OneTouchVital.Mana),
            ("/ra stamina", OneTouchVital.Stamina), ("/ra healme", OneTouchVital.Health),
        })
        {
            Ra(line);
            Check.True(r.B.OneTouchPending(vital), line + " requests " + vital);
            r.B.RequestOneTouch(vital);
            // Drain it without a heartbeat: the requests are plain flags.
            var at = (long[])typeof(BuffManager).GetField("_oneTouchAt", Any)!.GetValue(r.B)!;
            at[(int)vital] = 0;
        }

        // Before login there is no buff manager: said, not thrown.
        typeof(RynthAiPlugin).GetField("_buffManager", Any)!.SetValue(p, null);
        FakeHost.Chat.Clear();
        Ra("/ra heal");
        Check.True(Said("Buff manager not ready"), AllChat);
    }
}

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

// 2026-11-22: a melee character with Combat off, Heal At 0 and Emergency Heal At 0 stood among the
// monsters hitting it until it died: every hit made it "a fight", and in a fight the line is Heal At
// (0 = off). Now the fight line needs Combat on (BuffManager.FightHealthLine), and a heal held by the
// character's animation goes on after HealAnimationCapMs all told (BuffManager.HealAnimationCap.cs):
// hit recoils make the motion queue read busy, clear, busy, and each busy spell restarted the old limits.
internal static class HealUpSpeedTests
{
    private const uint Player = 0x500000C7;
    private const uint ListedKit = 0x80088CDA;
    private const uint Kit1 = 0x80073D1B;
    private const string KitName = "Treated Healing Kit";
    private const BindingFlags Any = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static;

    public static void Register(Runner r)
    {
        r.Add("heal up: Combat off, Heal At 0, a monster hitting, the animation busy between hits: a kit goes within 1.1 s", CombatOffHitAnimatingHealsFast);
        r.Add("heal up: Combat off, a monster in range: Top Off HP is the health line (not Heal At)", CombatOffUsesTopOffLine);
        r.Add("heal up: Combat on keeps Heal At in a fight; Heal At 0 with no emergency line heals nothing and says so", CombatOnFightLineUnchanged);
        r.Add("heal up (11-23): while Boost Loot's looting has the tick, Top Off HP is the health line and a kit goes; Heal At again once it hasn't", BoostLootLootingUsesTopOffLine);
        r.Add("heal up: the animation cap counts only animation holds, and resets once the vitals step is reached", AnimationCapCountsOnlyAnimation);
        r.Add("heal up: peace with the weapon in hand gets the weapon-out kit bar (30%); the wand in hand or Magic keep Kit Min Success", PeaceWeaponOutKitBar);
        r.Add("heal up: [HealLatency] ends an episode at a macro stop; the heal after the restart counts from the restart", HealLatencyMacroOff);
    }

    private static void HealLatencyMacroOff()
    {
        var logs = new System.Collections.Generic.List<string>();
        var t = new HealLatencyTracker(logs.Add);
        var t0 = new DateTime(2026, 11, 22, 15, 10, 52);
        t.Observe(t0, 20, "Top Off HP", 80, false, false);
        t.EndTick(t0, "", "", "Buffing");
        t.MacroOff(t0.AddSeconds(12), 20);
        Check.True(logs.Any(l => l.Contains("then the macro stopped at 20%", StringComparison.Ordinal)), "the stop ends the episode, said once: " + string.Join(" | ", logs));
        t.MacroOff(t0.AddSeconds(12.1), 20);
        Check.Eq(logs.Count, 1, "said once");
        var t1 = t0.AddSeconds(12.5);
        t.Observe(t1, 20, "Top Off HP", 80, false, false);
        t.HealSent(t1.AddMilliseconds(150), "Eternal Health Kit");
        Check.True(t.LastDelayMs is >= 149 and <= 151, $"measured from the restart: {t.LastDelayMs} ms");
    }

    private const uint Wand = 0x80073D2C;

    private static bool KitChance(BuffManager b, uint missing, int minPct)
        => (bool)typeof(BuffManager).GetMethod("KitChanceOk", Any)!
            .Invoke(b, new object[] { unchecked((int)Kit1), KitName, missing, false, minPct, true })!;

    private static void PeaceWeaponOutKitBar()
    {
        foreach (var (mode, wandInHand, expect, what) in new[]
        {
            (CombatMode.NonCombat, false, true, "peace, the weapon in hand: the 30% bar, the 53% kit goes"),
            (CombatMode.NonCombat, true, false, "peace, the wand in hand: Kit Min Success 70% (no swap to save)"),
            (CombatMode.Magic, true, false, "Magic stance: Kit Min Success 70%"),
            (CombatMode.Melee, false, true, "melee: the 30% bar, as before"),
        })
        {
            try
            {
                var (b, s, v, h) = Rig(enableCombat: true, healAt: 50);
                FakeHost.CombatModeValue = mode;
                FakeHost.Skills[(Player, 21)] = (175, 2);   // Healing: (175 + 25) x 1.1 = 220 vs 108 missing x 2 = 216 in peace: 53%
                FakeHost.Names[Wand] = "Wand";
                FakeHost.ItemTypes[Wand] = 0x8000;
                FakeHost.Appraised.Add(Wand);
                if (wandInHand) FakeHost.Ints[(Wand, 10)] = 0x01000000;   // CurrentWieldedLocation: Held
                s.ItemRules.Add(new ItemRule { Id = unchecked((int)Wand), Name = "Wand", Element = "Fire" });
                b.SetWorldObjectCache(FakeHost.MakeCache(h, Player, new[] { Kit1, Wand }));
                var skills = new CharacterSkills(h);
                skills.SetPlayerId(Player);
                b.SetCharacterSkills(skills);
                uint missing = mode == CombatMode.NonCombat ? 108u : 98u;   // ~53% in either stance (x 1.1 out of peace)
                Check.Eq(KitChance(b, missing, 70), expect, what + ": " + string.Join(" | ", FakeHost.Logs.Where(l => l.Contains("would work"))));
            }
            finally { Done(); }
        }
    }

    private static DateTime _now;
    private static long _ms;

    private static void Advance(int ms) { _now = _now.AddMilliseconds(ms); _ms += ms; }

    private static (BuffManager B, LegacyUiSettings S, PlayerVitalsCache V, RynthCoreHost H) Rig(bool enableCombat, int healAt = 0)
    {
        FakeHost.Reset();
        FakeHost.WeaponCalls = true;
        FakeHost.ServerGates = true;          // GetCastBusyState (the animation) and the UseDone sequence
        FakeHost.LastUseDoneCalls = true;
        FakeHost.UseOnCalls = true;
        FakeHost.BusyCalls = true;
        FakeHost.CombatModeValue = CombatMode.Melee;
        FakeHost.Names[Kit1] = KitName;
        FakeHost.Skills[(Player, 33)] = (10, 1);   // Life Magic untrained: the kit is the heal
        var host = FakeHost.Create(Player);
        _now = new DateTime(2026, 11, 22, 15, 13, 15);
        _ms = 50_000_000;
        RynthClock.TestNow = () => _now;
        ActionPacer.Reset();
        ActionPacer.Enabled = true;
        ActionPacer.NowMs = () => _ms;
        UseAudit.Reset("RynthAi", () => true);
        UseAudit.NowMs = () => _ms;
        MoveAudit.Reset();
        MoveAudit.NowMs = () => _ms;
        var rec = typeof(RynthAiPlugin).GetMethod("RecordUseForPacing", Any)!;
        UseAudit.AfterSend = (h, u) => rec.Invoke(null, new object[] { h, u });
        var s = new LegacyUiSettings
        {
            IsMacroRunning = true, EnableCombat = enableCombat, HealAt = healAt, TopOffHP = 80, EmergencyHealAt = 0,
            StaminaToHealthAt = 0, GetManaAt = 0, TopOffMana = 0, RestamAt = 0, TopOffStam = 0, UsePotions = false,
            EnableBuffing = true, KitMinSuccessPct = 0, PeaceModeForKits = false,
        };
        s.ConsumableRules.Add(new ConsumableRule { Id = unchecked((int)ListedKit), Name = KitName, Type = "HealthKit" });
        var skills = new CharacterSkills(host);
        skills.SetPlayerId(Player);
        var sm = new SpellManager(host, s);
        sm.InitializeNatively();
        sm.SetCharacterSkills(skills);
        var v = new PlayerVitalsCache { MaxHealth = 500, CurrentHealth = 400, MaxStamina = 100, CurrentStamina = 100, MaxMana = 100, CurrentMana = 100 };
        var b = new BuffManager(host, s, sm, v);
        b.SetCharacterSkills(skills);
        b.SetWorldObjectCache(FakeHost.MakeCache(host, Player, new[] { Kit1 }));
        b.MarkLiveBuffsReadyForTests();
        b.Clock = () => _now;
        return (b, s, v, host);
    }

    private static void Done()
    {
        RynthClock.TestNow = null;
        ActionPacer.Enabled = false;
        ActionPacer.Reset();
        ActionPacer.NowMs = () => Environment.TickCount64;
        UseAudit.Reset("?", null);
        UseAudit.NowMs = () => Environment.TickCount64;
        UseAudit.AfterSend = null;
        MoveAudit.Reset();
        MoveAudit.NowMs = () => Environment.TickCount64;
        FakeHost.CastBusy = 0;
    }

    private static bool KitSent => FakeHost.UsesOn.Any(u => u.Source == Kit1 && u.Target == Player);

    private static void CombatOffHitAnimatingHealsFast()
    {
        try
        {
            var (b, s, v, h) = Rig(enableCombat: false);
            b.InCombatOverride = () => true;   // a monster within Monster Range, hitting
            // Health falls under Top Off HP 80%: 400 -> 380 (76%), then a hit every 500 ms.
            v.CurrentHealth = 380;
            int firstUnderMs = -1, sentMs = -1;
            for (int t = 0; t <= 3000; t += 100)
            {
                // A hit recoil: busy at every vitals tick; between ticks another reader (the item step)
                // sees the queue clear for a moment, which restarts the pacer's busy spell.
                FakeHost.CastBusy = 0;
                ActionPacer.GestureHolds(h, "Items", HealNeed.None, out _);
                FakeHost.CastBusy = 1;
                if (t > 0 && t % 500 == 0 && v.CurrentHealth > 60) v.CurrentHealth -= 40;
                if (firstUnderMs < 0) firstUnderMs = t;
                b.OnHeartbeat();
                if (KitSent) { sentMs = t; break; }
                Advance(100);
            }
            Check.True(sentMs >= 0, "a kit went out: " + string.Join(" | ", FakeHost.Logs.Where(l => l.Contains("Vitals") || l.Contains("[Pace]")).Take(12)));
            Check.True(sentMs - firstUnderMs <= 1100, $"within 1.1 s of going under the line (took {sentMs - firstUnderMs} ms)");
            Check.True(FakeHost.Logs.Any(l => l.Contains("on the character's animation", StringComparison.Ordinal)
                                              && l.Contains("under Top Off HP 80%", StringComparison.Ordinal)), "the cap is logged, with the Top Off line");
            Check.Eq(b.HealAnimationCaps, 1, "one cap");
        }
        finally { Done(); }
    }

    private static void CombatOffUsesTopOffLine()
    {
        try
        {
            var (b, s, v, h) = Rig(enableCombat: false, healAt: 30);
            b.InCombatOverride = () => true;
            v.CurrentHealth = 300;   // 60%: over Heal At 30, under Top Off HP 80
            Check.True(b.WantsHealNow(), "Combat off: Top Off HP 80% applies with a monster in range");
            Check.True(b.CheckVitals(), "the vitals act");
            Check.True(KitSent, "a kit went out");
        }
        finally { Done(); }
    }

    private static void BoostLootLootingUsesTopOffLine()
    {
        try
        {
            var (b, s, v, h) = Rig(enableCombat: true, healAt: 30);
            b.InCombatOverride = () => true;   // a monster within Monster Range
            bool looting = true;
            b.BoostLootHealsToTopOff = () => looting;
            v.CurrentHealth = 300;   // 60%: over Heal At 30, under Top Off HP 80
            Check.True(b.WantsHealNow(), "Boost Loot looting: Top Off HP 80% is the line");
            Check.True(b.CheckVitals(), "the vitals act");
            Check.True(KitSent, "a kit went out: " + string.Join(" | ", FakeHost.Logs.Where(l => l.Contains("Vitals")).Take(6)));

            var (b2, s2, v2, h2) = Rig(enableCombat: true, healAt: 30);
            b2.InCombatOverride = () => true;
            b2.BoostLootHealsToTopOff = () => false;   // the looting doesn't have the tick (no loot work, or Boost Loot off)
            v2.CurrentHealth = 300;
            Check.False(b2.WantsHealNow(), "not looting: Heal At 30% is the fight's line, as before");
            b2.CheckVitals();
            Check.False(KitSent, "no kit");

            b2.BoostLootHealsToTopOff = () => throw new InvalidOperationException("hook threw");
            Check.False(b2.WantsHealNow(), "a hook that throws counts as not looting");
        }
        finally { Done(); }
    }

    private static void CombatOnFightLineUnchanged()
    {
        try
        {
            var (b, s, v, h) = Rig(enableCombat: true, healAt: 30);
            b.InCombatOverride = () => true;
            v.CurrentHealth = 300;   // 60%
            Check.False(b.WantsHealNow(), "Combat on, in a fight: Heal At 30% is the line, 60% is over it");
            v.CurrentHealth = 140;   // 28%
            Check.True(b.WantsHealNow(), "under Heal At: heal");

            var (b2, s2, v2, h2) = Rig(enableCombat: true, healAt: 0);
            b2.InCombatOverride = () => true;
            v2.CurrentHealth = 50;   // 10%
            Check.False(b2.WantsHealNow(), "Heal At 0 and no emergency line: nothing heals in a fight (the setting)");
            b2.CheckVitals();
            Check.False(KitSent, "no kit");
            Check.Eq(FakeHost.Logs.Count(l => l.Contains("no heal line covers fights", StringComparison.Ordinal)), 1, "said once in the log");
            Check.Eq(FakeHost.Chat.Count(c => c.Contains("no heal line covers fights", StringComparison.Ordinal)), 1, "and once in chat");
            Advance(10_000);
            b2.CheckVitals();
            Check.Eq(FakeHost.Logs.Count(l => l.Contains("no heal line covers fights", StringComparison.Ordinal)), 1, "not again within a minute");
        }
        finally { Done(); }
    }

    private static void AnimationCapCountsOnlyAnimation()
    {
        try
        {
            var (b, s, v, h) = Rig(enableCombat: false);
            b.InCombatOverride = () => true;
            v.CurrentHealth = 380;
            // Animation for 600 ms, then a clear read in our own tick: the vitals step is reached and the
            // kit goes; no cap.
            FakeHost.CastBusy = 1;
            for (int t = 0; t < 600; t += 100) { b.OnHeartbeat(); Advance(100); }
            Check.False(KitSent, "held by the animation for 600 ms");
            FakeHost.CastBusy = 0;
            b.OnHeartbeat();
            Check.True(KitSent, "the animation ended: the kit goes");
            Check.Eq(b.HealAnimationCaps, 0, "no cap: the gate opened by itself");
            // Health over every line: nothing counts.
            FakeHost.UsesOn.Clear();
            v.CurrentHealth = 450;
            FakeHost.CastBusy = 1;
            for (int t = 0; t < 2000; t += 100) { b.OnHeartbeat(); Advance(100); }
            Check.Eq(b.HealAnimationCaps, 0, "no heal due: no cap");
        }
        finally { Done(); }
    }
}

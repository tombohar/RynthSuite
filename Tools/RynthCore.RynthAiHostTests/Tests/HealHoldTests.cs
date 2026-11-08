using System;
using System.Linq;
using RynthCore.Plugin.RynthAi;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.PluginSdk;
using RynthCore.RynthAiHostTests.Fakes;
using RynthCore.RynthAiTests.Harness;

namespace RynthCore.RynthAiHostTests.Tests;

// The Buffing hold: health under Heal At held the
// arbiter on Buffing for 17 s with no heal landing (a used-up kit re-sent), the character stopped
// attacking the boss and died. In a fight, 6 s under a heal line with no heal landing now hands combat
// the tick for 5 s (BuffManager.HealHold.cs).
internal static class HealHoldTests
{
    private const uint Player = 0x500000B6;
    private const uint ListedKit = 0x80088BDA;   // the stack the Consumables rule was added from
    private const uint Kit1 = 0x80073C1B;
    private const uint Kit2 = 0x80073C1A;
    private const string KitName = "Treated Healing Kit";

    public static void Register(Runner r)
    {
        r.Add("heal hold: 6 s under a heal line with no heal landing hands combat the tick for 5 s; a heal landing holds it again", HealHoldReleases);
        r.Add("heal hold: while combat has the tick a heal makes no stance change or wand swap; a caster in Magic still casts", HealHoldNoStanceChange);
        r.Add("heal hold: a macro stop resets the hold; the first heartbeat after a restart in a fight does not release", HealHoldResetsOnMacroStop);
        r.Add("heal hold: no release at 6 s while a kit in flight is animating; a kit that never animates still releases", HealHoldWaitsForAnimatingKit);
        r.Add("heal hold: no release at 6 s while a wand wield is in flight; released once it is done", HealHoldWaitsForWield);
        r.Add("heal hold: while combat has the tick, Kit Min Success is computed in the current stance, not peace", ReleasedKitChanceNotPeace);
        r.Add("heal hold: the release logs its start, its end and the first wand swap it refuses (one line each)", HealHoldLogLines);
        r.Add("heal hold: while combat has the tick, under Emergency Heal At the kit despite Kit Min Success still goes", ReleasedEmergencyLastResortKit);
    }

    private static void HealHoldLogLines()
    {
        var b = MeleeSpellRig(out var v, out var setNow);
        var t0 = new DateTime(2026, 1, 1, 12, 0, 50);
        setNow(t0); b.OnHeartbeat();
        setNow(t0.AddMilliseconds(6000)); b.OnHeartbeat();
        Check.Eq(FakeHost.Logs.Count(l => l.Contains("[RynthAi] Vitals: heal hold started", StringComparison.Ordinal)), 1, "the start is logged once");
        setNow(t0.AddMilliseconds(6500)); b.CheckVitals();
        setNow(t0.AddMilliseconds(7000)); b.CheckVitals();
        Check.Eq(FakeHost.Logs.Count(l => l.Contains("heal hold: no wand swap for Heal Self while combat has the tick", StringComparison.Ordinal)), 1,
            "the refused wand swap is logged once per release: " + string.Join(" | ", FakeHost.Logs.Where(l => l.Contains("heal hold", StringComparison.Ordinal))));
        setNow(t0.AddMilliseconds(11_000)); b.OnHeartbeat();
        Check.True(FakeHost.Logs.Any(l => l.Contains("heal hold ended (its 5 s are over", StringComparison.Ordinal)), "the window's end is logged");
        setNow(t0.AddMilliseconds(17_000)); b.OnHeartbeat();
        Check.Eq(FakeHost.Logs.Count(l => l.Contains("heal hold started", StringComparison.Ordinal)), 2, "the second release is logged");
        v.CurrentHealth = 90;
        setNow(t0.AddMilliseconds(18_000)); b.OnHeartbeat();
        Check.True(FakeHost.Logs.Any(l => l.Contains("heal hold ended (health 90% is over the heal lines)", StringComparison.Ordinal)), "health back over the line ends it, logged");
    }

    // The safety floor: a release refuses stance changes, never the last-resort kit.
    private static void ReleasedEmergencyLastResortKit()
    {
        var b = KitRig();
        FakeHost.Skills[(Player, 21)] = (10, 2);   // Healing low: the kit's chance is under any real Kit Min Success
        RigSettings.KitMinSuccessPct = 95;
        RigSettings.EmergencyHealAt = 30;
        SetHealth(b, 20);
        var t0 = new DateTime(2026, 1, 1, 12, 1, 3);
        b.Clock = () => t0;
        typeof(BuffManager).GetField("_hhReleasedUntil", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(b, t0.AddMilliseconds(5000));   // combat has the tick
        Check.True(b.HealHoldReleased, "the rig: released");
        Check.True(b.CheckVitals(), "20% under Emergency Heal At 30%: the vitals act");
        Check.Eq(FakeHost.UsesOn.Count, 1, "a kit goes out");
        Check.True(FakeHost.Logs.Any(l => l.Contains("despite Kit Min Success", StringComparison.Ordinal)),
            "the kit despite Kit Min Success (logged): " + string.Join(" | ", FakeHost.Logs.Where(l => l.Contains("Vitals", StringComparison.Ordinal))));
    }

    private static BuffManager MeleeSpellRig(out PlayerVitalsCache v, out Func<DateTime, DateTime> setNow, int mode = CombatMode.Melee,
        bool monsterInRange = true)
    {
        FakeHost.Reset();
        FakeHost.WeaponCalls = true;
        FakeHost.CombatModeCalls = true;
        FakeHost.CombatModeValue = mode;
        foreach (var row in SpellTable.Rows) FakeHost.Spellbook.Add((uint)row.Id);
        FakeHost.Skills[(Player, 33)] = (400, 2);   // Life Magic
        var host = FakeHost.Create(Player);
        var s = new LegacyUiSettings
        {
            IsMacroRunning = true, HealAt = 75, TopOffHP = 75, EmergencyHealAt = 0, StaminaToHealthAt = 0,
            GetManaAt = 0, TopOffMana = 0, RestamAt = 0, TopOffStam = 0, UsePotions = false, EnableBuffing = false,
        };
        var skills = new CharacterSkills(host);
        skills.SetPlayerId(Player);
        var sm = new SpellManager(host, s);
        sm.InitializeNatively();
        sm.SetCharacterSkills(skills);
        v = new PlayerVitalsCache { MaxHealth = 100, CurrentHealth = 48, MaxStamina = 100, CurrentStamina = 100, MaxMana = 100, CurrentMana = 100 };
        var b = new BuffManager(host, s, sm, v);
        b.SetCharacterSkills(skills);
        b.MarkLiveBuffsReadyForTests();
        b.InCombatOverride = () => monsterInRange;   // the boss within Monster Range
        DateTime now = new DateTime(2026, 1, 1, 12, 0, 50);
        b.Clock = () => now;
        setNow = t => now = t;
        RigSettings = s;
        return b;
    }

    /// <summary>The settings of the last rig built (MeleeSpellRig or KitRig).</summary>
    private static LegacyUiSettings RigSettings = new();

    /// <summary>A melee character with two healing kits and Life Magic untrained: kits are the only heal.</summary>
    private static BuffManager KitRig(int mode = CombatMode.Melee, bool modeCalls = false)
    {
        FakeHost.Reset();
        FakeHost.WeaponCalls = true;
        FakeHost.CombatModeCalls = modeCalls;
        FakeHost.ServerGates = true;
        FakeHost.LastUseDoneCalls = true;
        FakeHost.UseOnCalls = true;
        FakeHost.CombatModeValue = mode;
        FakeHost.Names[Kit1] = KitName;
        FakeHost.Names[Kit2] = KitName;
        // Life Magic untrained (a read, not the capable stub): kits are the only heal, no Heal Self.
        FakeHost.Skills[(Player, 33)] = (10, 1);
        var host = FakeHost.Create(Player);
        var s = new LegacyUiSettings
        {
            IsMacroRunning = true, HealAt = 60, TopOffHP = 60, EmergencyHealAt = 0, StaminaToHealthAt = 0,
            GetManaAt = 0, TopOffMana = 0, RestamAt = 0, TopOffStam = 0, UsePotions = false, EnableBuffing = false,
            KitMinSuccessPct = 0, PeaceModeForKits = false,
        };
        s.ConsumableRules.Add(new ConsumableRule { Id = unchecked((int)ListedKit), Name = KitName, Type = "HealthKit" });
        var skills = new CharacterSkills(host);
        skills.SetPlayerId(Player);
        var sm = new SpellManager(host, s);
        sm.InitializeNatively();
        sm.SetCharacterSkills(skills);
        var v = new PlayerVitalsCache { MaxHealth = 100, CurrentHealth = 40, MaxStamina = 100, CurrentStamina = 100, MaxMana = 100, CurrentMana = 100 };
        var b = new BuffManager(host, s, sm, v);
        b.SetCharacterSkills(skills);
        b.SetWorldObjectCache(FakeHost.MakeCache(host, Player, new[] { Kit1, Kit2 }));
        RigSettings = s;
        return b;
    }

    private static void SetHealth(BuffManager b, int hp)
    {
        var f = typeof(BuffManager).GetField("_vitals", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        ((PlayerVitalsCache)f.GetValue(b)!).CurrentHealth = (uint)hp;
    }

    // HeartbeatCore returns before TickHealHold while the macro is off, so the hold's timer must be reset
    // there: a restart in a fight (the HP failsafe's recall turning it back on, too) released at once.
    private static void HealHoldResetsOnMacroStop()
    {
        var b = MeleeSpellRig(out _, out var setNow, CombatMode.Magic);
        var t0 = new DateTime(2026, 1, 1, 12, 0, 50);
        setNow(t0); b.OnHeartbeat();
        setNow(t0.AddMilliseconds(5000)); b.OnHeartbeat();
        Check.True(b.WantsVitalRecharge, "5 s under the line: held");
        RigSettings.IsMacroRunning = false;
        setNow(t0.AddMilliseconds(10_000)); b.OnHeartbeat();
        RigSettings.IsMacroRunning = true;
        setNow(t0.AddMilliseconds(15_000)); b.OnHeartbeat();
        Check.True(b.WantsVitalRecharge, "macro back on in the fight: the vitals hold the tick (a fresh count)");
        Check.Eq(b.HealHoldReleases, 0, "no release on the first heartbeat after the restart");
        setNow(t0.AddMilliseconds(20_900)); b.OnHeartbeat();
        Check.True(b.WantsVitalRecharge, "5.9 s after the restart: still held");
        setNow(t0.AddMilliseconds(21_000)); b.OnHeartbeat();
        Check.False(b.WantsVitalRecharge, "6 s after the restart with nothing landing: released");
    }

    private static void HealHoldReleases()
    {
        // A caster in Magic stance: its Heal Self goes out with no stance step, so nothing but the
        // hold itself (no wand-flip backoff) decides what WantsVitalRecharge reads.
        var b = MeleeSpellRig(out var v, out var setNow, CombatMode.Magic);
        var t0 = new DateTime(2026, 1, 1, 12, 0, 50);
        setNow(t0);
        b.OnHeartbeat();
        Check.True(b.WantsVitalRecharge, "48% under Heal At 75%: vitals want the tick");
        setNow(t0.AddMilliseconds(5900)); b.OnHeartbeat();
        Check.True(b.WantsVitalRecharge, "5.9 s with no heal landing: still held");
        setNow(t0.AddMilliseconds(6000)); b.OnHeartbeat();
        Check.False(b.WantsVitalRecharge, "6 s with no heal landing: combat gets the tick");
        Check.True(FakeHost.Logs.Any(l => l.Contains("no heal landed", StringComparison.Ordinal)), "logged");
        setNow(t0.AddMilliseconds(10_900)); b.OnHeartbeat();
        Check.False(b.WantsVitalRecharge, "4.9 s into the 5 s window: combat still has it");
        setNow(t0.AddMilliseconds(11_000)); b.OnHeartbeat();
        Check.True(b.WantsVitalRecharge, "the window over: vitals hold the tick again");
        setNow(t0.AddMilliseconds(16_900)); b.OnHeartbeat();
        Check.True(b.WantsVitalRecharge, "5.9 s into the next hold: held");
        setNow(t0.AddMilliseconds(17_000)); b.OnHeartbeat();
        Check.False(b.WantsVitalRecharge, "6 s again with nothing landing: released again");

        // A heal landing (health up 3 points or more) ends a release and restarts the count.
        v.CurrentHealth = 52;
        setNow(t0.AddMilliseconds(18_000)); b.OnHeartbeat();
        Check.True(b.WantsVitalRecharge, "a heal landed (48 -> 52): the vitals hold the tick again");
        v.CurrentHealth = 50;
        setNow(t0.AddMilliseconds(23_900)); b.OnHeartbeat();
        Check.True(b.WantsVitalRecharge, "5.9 s after the landing: held");
        setNow(t0.AddMilliseconds(24_000)); b.OnHeartbeat();
        Check.False(b.WantsVitalRecharge, "6 s after it with nothing more: released");

        // Over the line ends the episode; a new dip starts a fresh 6 s count.
        v.CurrentHealth = 90;
        setNow(t0.AddMilliseconds(25_000)); b.OnHeartbeat();
        v.CurrentHealth = 40;
        setNow(t0.AddMilliseconds(26_000)); b.OnHeartbeat();
        Check.True(b.WantsVitalRecharge, "a new dip: held from the start");
        setNow(t0.AddMilliseconds(31_900)); b.OnHeartbeat();
        Check.True(b.WantsVitalRecharge, "5.9 s into the new dip: held (the count restarted)");
        setNow(t0.AddMilliseconds(32_000)); b.OnHeartbeat();
        Check.False(b.WantsVitalRecharge, "6 s into the new dip: released");

        // No monster within Monster Range: nothing to fight, the vitals keep the tick.
        var q = MeleeSpellRig(out _, out var setNowQ, CombatMode.Magic, monsterInRange: false);
        setNowQ(t0); q.OnHeartbeat();
        setNowQ(t0.AddMilliseconds(20_000)); q.OnHeartbeat();
        Check.True(q.WantsVitalRecharge, "no monster in range: 20 s with no heal landing, still held (nav and loot don't get it)");
        Check.Eq(q.HealHoldReleases, 0, "never released");
    }

    private static void HealHoldNoStanceChange()
    {
        var b = MeleeSpellRig(out _, out var setNow);
        var t0 = new DateTime(2026, 1, 1, 12, 0, 50);
        setNow(t0);
        Check.True(b.CheckVitals(), "control: melee, no kit: Heal Self starts its stance change");
        Check.Eq(FakeHost.CombatModeRequests.Count(m => m == CombatMode.Magic), 1, "control: Magic mode was asked for");
        b.OnHeartbeat();
        int before = FakeHost.CombatModeRequests.Count;
        int wieldsBefore = FakeHost.Wields.Count;
        // The heartbeat that releases the hold runs the vitals step too (the flip's 2 s backoff is over).
        setNow(t0.AddMilliseconds(6000)); b.OnHeartbeat();
        Check.False(b.WantsVitalRecharge, "the rig: combat has the tick");
        setNow(t0.AddMilliseconds(7000));
        b.CheckVitals();
        Check.Eq(FakeHost.CombatModeRequests.Count, before, "while combat has the tick: no stance change for Heal Self");
        Check.Eq(FakeHost.Wields.Count, wieldsBefore, "and no wield");

        // A caster already in Magic stance needs no change: its Heal Self still goes.
        var c = MeleeSpellRig(out _, out var setNowC, CombatMode.Magic);
        setNowC(t0);
        c.OnHeartbeat();
        setNowC(t0.AddMilliseconds(6000)); c.OnHeartbeat();
        Check.False(c.WantsVitalRecharge, "caster rig: combat has the tick");
        int casts = FakeHost.Casts.Count;
        c.CheckVitals();
        Check.True(FakeHost.Casts.Count > casts, "caster in Magic: Heal Self is cast while combat has the tick");

        // A caster in Magic stance with Use Kits in Magic Mode and Peace Mode for Kits: the kit's peace
        // switch is a stance change too. Control first (not released): it goes out.
        foreach (bool released in new[] { false, true })
        {
            var k = KitRig(CombatMode.Magic, modeCalls: true);
            RigSettings.UseKitsInMagicMode = true;
            RigSettings.PeaceModeForKits = true;
            var t1 = new DateTime(2026, 1, 1, 12, 1, 0);
            k.Clock = () => t1;
            if (released)
                typeof(BuffManager).GetField("_hhReleasedUntil", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                    .SetValue(k, t1.AddMilliseconds(5000));
            k.CheckVitals();
            int peace = FakeHost.CombatModeRequests.Count(m => m == CombatMode.NonCombat);
            if (released)
            {
                Check.Eq(peace, 0, "Magic stance, combat has the tick: no peace switch for the kit");
                Check.Eq(FakeHost.UsesOn.Count, 1, "the kit is used in the stance it is in");
            }
            else
                Check.Eq(peace, 1, "control, Magic stance, not released: the kit's peace switch goes out");
        }
    }

    // A release must not cut off a heal the server is already playing. A kit that has animated
    // (heal grace's NoteKitProgress), or a wand wield in flight, holds the release back; a removed kit
    // never animates, so it still releases.
    private static void HealHoldWaitsForAnimatingKit()
    {
        foreach (bool animating in new[] { true, false })
        {
            string how = animating ? "animating kit" : "kit with no motion";
            var b = KitRig();
            b.InCombatOverride = () => true;
            var t0 = new DateTime(2026, 1, 1, 12, 0, 58);
            DateTime now = t0;
            b.Clock = () => now;
            FakeHost.UseDoneSeq = 10;
            b.OnHeartbeat();                       // the hold starts counting
            if (FakeHost.UsesOn.Count == 0) b.CheckVitals();
            Check.Eq(FakeHost.UsesOn.Count, 1, $"{how}: the kit is in flight");
            FakeHost.CastBusy = animating ? 1 : 0;
            now = t0.AddMilliseconds(6000);
            b.OnHeartbeat();
            FakeHost.CastBusy = 0;
            if (animating)
            {
                Check.Eq(b.HealHoldReleases, 0, "6 s, the kit's heal motion playing: no release");
                Check.True(b.WantsVitalRecharge, "the vitals keep the tick for it");
            }
            else
                Check.Eq(b.HealHoldReleases, 1, "6 s, a kit the server never animated (the live failure): released");
        }
    }

    private static void HealHoldWaitsForWield()
    {
        var b = MeleeSpellRig(out _, out var setNow);
        var t0 = new DateTime(2026, 1, 1, 12, 0, 50);
        var pending = typeof(BuffManager).GetField("_pendingWieldId", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        setNow(t0); b.OnHeartbeat();
        pending.SetValue(b, unchecked((int)0x80084C1E));   // the wand's wield sent, the server not done yet
        setNow(t0.AddMilliseconds(6000)); b.OnHeartbeat();
        Check.Eq(b.HealHoldReleases, 0, "6 s, a wand wield in flight: no release");
        Check.True(b.WantsVitalRecharge, "the vitals keep the tick");
        pending.SetValue(b, 0);
        setNow(t0.AddMilliseconds(6100)); b.OnHeartbeat();
        Check.Eq(b.HealHoldReleases, 1, "the wield done, still nothing landed: released");
    }

    private static void ReleasedKitChanceNotPeace()
    {
        var b = KitRig();
        FakeHost.Skills[(Player, 21)] = (75, 2);   // Healing (STypeSkill 21), trained
        RigSettings.PeaceModeForKits = true;
        RigSettings.KitMinSuccessPct = 30;
        SetHealth(b, 34);                          // 66 missing
        var t0 = new DateTime(2026, 1, 1, 12, 1, 3);
        b.Clock = () => t0;
        typeof(BuffManager).GetField("_hhReleasedUntil", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(b, t0.AddMilliseconds(5000));   // combat has the tick
        Check.True(b.HealHoldReleased, "the rig: released");
        b.CheckVitals();
        // (75 + 25) x 1.1 = 110 against 66 x 2 x 1.1 = 145 in melee: 25%, under 30. In peace it would be
        // 66 x 2 = 132: 34%, over it. No peace switch goes out while released, so melee is the stance.
        Check.True(FakeHost.Logs.Any(l => l.Contains("would work 25% of the time on 66 missing (skill 110 vs 145)", StringComparison.Ordinal)),
            "the chance is the melee one: " + string.Join(" | ", FakeHost.Logs.Where(l => l.Contains("would work", StringComparison.Ordinal))));
    }
}

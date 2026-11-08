using System;
using System.Linq;
using System.Threading;
using RynthCore.Plugin.RynthAi;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.PluginSdk;
using RynthCore.RynthAiHostTests.Fakes;
using RynthCore.RynthAiTests.Harness;

namespace RynthCore.RynthAiHostTests.Tests;

// A kit its own result line calls used up ("... Your Treated Healing Kit is used up.", ACE
// Healer.DoHealing) is skipped by every kit pick (BuffManager.KitUsedUp.cs). The picks read the client's
// live pack walk, which drops the removed kit, but reuses a walk for up to 1 s, and the next kit goes out
// 700 ms after the line: inside that reuse the used-up kit was picked again (checked on main: the same
// rig picks Kit1). These rigs freeze the walk's clock (WorldObjectCache.WalkClock), so the walk is never
// refreshed and only the skip can keep the used-up kit out. Without GetContainerContents there is no walk
// at all, and the skip does not end.
internal static class KitUsedUpTests
{
    private const uint Player = 0x500000C1;
    private const uint GoneListed = 0x80088BDA;   // the stack the Consumables rule was added from, long gone
    private const uint Kit1 = 0x80073C1B;
    private const uint Kit2 = 0x80073C1A;
    private const uint Gem = 0x80073C19;          // something else carried, so the walk is never empty
    private const uint StamKit1 = 0x80073C18, StamKit2 = 0x80073C17;
    private const string KitName = "Treated Healing Kit";

    public static void Register(Runner r)
    {
        r.Add("kit used up: its result line skips that kit inside the pack walk's reuse; the next kit goes (rule and pack picks)", UsedUpLineNextKit);
        r.Add("kit used up: a line with uses left keeps the kit (control)", UsesLeftKeepsKit);
        r.Add("kit used up: a line naming another kit marks nothing; naming the kit in flight marks it", OtherKitLineIgnored);
        r.Add("kit used up: with the pack walk the skip ends after 3 s (the BuffManager clock)", SkipExpires);
        r.Add("kit used up: without GetContainerContents the skip does not end (no walk ever drops the kit)", NoWalkSkipLasts);
        r.Add("kit used up: a stamina kit's used-up line skips it for the next stamina kit", StaminaKitUsedUp);
    }

    private static BuffManager Rig(bool withRule, string kit1Name = KitName, bool walk = true)
    {
        FakeHost.Reset();
        FakeHost.WeaponCalls = true;
        FakeHost.UseOnCalls = true;
        FakeHost.ContentsCalls = walk;               // the client's pack walk (GetContainerContents)
        FakeHost.CombatModeValue = CombatMode.Melee;
        FakeHost.Names[Kit1] = kit1Name;
        FakeHost.Names[Kit2] = KitName;
        FakeHost.Names[Gem] = "Sapphire";
        FakeHost.Containers[Kit1] = Player;
        FakeHost.Containers[Kit2] = Player;
        FakeHost.Containers[Gem] = Player;
        FakeHost.Skills[(Player, 33)] = (10, 1);     // Life Magic untrained: kits are the only heal
        var host = FakeHost.Create(Player);
        var s = new LegacyUiSettings
        {
            IsMacroRunning = true, HealAt = 60, TopOffHP = 60, EmergencyHealAt = 0, StaminaToHealthAt = 0,
            GetManaAt = 0, TopOffMana = 0, RestamAt = 0, TopOffStam = 0, UsePotions = false, EnableBuffing = false,
            KitMinSuccessPct = 0, PeaceModeForKits = false,
        };
        if (withRule)
            s.ConsumableRules.Add(new ConsumableRule { Id = unchecked((int)GoneListed), Name = KitName, Type = "HealthKit" });
        var skills = new CharacterSkills(host);
        skills.SetPlayerId(Player);
        var sm = new SpellManager(host, s);
        sm.InitializeNatively();
        sm.SetCharacterSkills(skills);
        var v = new PlayerVitalsCache { MaxHealth = 100, CurrentHealth = 40, MaxStamina = 100, CurrentStamina = 100, MaxMana = 100, CurrentMana = 100 };
        var b = new BuffManager(host, s, sm, v);
        b.SetCharacterSkills(skills);
        b.SetWorldObjectCache(Frozen(FakeHost.MakeCache(host, Player, new[] { Kit1, Kit2, Gem })));
        return b;
    }

    /// <summary>The walk's reuse clock stopped: the first walk is reused for the whole test.</summary>
    private static WorldObjectCache Frozen(WorldObjectCache cache)
    {
        var frozen = new DateTime(2026, 1, 1, 12, 0, 0);
        cache.WalkClock = () => frozen;
        return cache;
    }

    private static uint LastKit() => FakeHost.UsesOn.Count == 0 ? 0 : FakeHost.UsesOn[^1].Source;

    /// <summary>The server removed the kit: the client deletes it (no pack, no name); the cache never hears.</summary>
    private static void Gone(uint id) { FakeHost.Containers.Remove(id); FakeHost.Names.Remove(id); }

    /// <summary>The kit's result line, then past KitLandGraceMs (real clock): the kit wait ends.</summary>
    private static void ResultLine(BuffManager b, string line)
    {
        b.OnChatWindowText(line, 0);
        Thread.Sleep(750);
        Check.False(b.KitPending(), "the result line and 700 ms: the kit wait ends");
    }

    private static void UsedUpLineNextKit()
    {
        foreach (bool withRule in new[] { true, false })
        {
            string how = withRule ? "Consumables rule" : "best pack kit";
            var b = Rig(withRule);
            Check.True(b.CheckVitals(), $"{how}: 40% under Heal At 60%: acts");
            Check.Eq(LastKit(), Kit1, $"{how}: Kit1 first (the walk is read now)");
            Gone(Kit1);
            ResultLine(b, $"You heal yourself for 12 Health points. Your {KitName} is used up.");
            Check.Eq(b.KitsUsedUp, 1, $"{how}: the kit in flight is marked used up");
            Check.True(b.CheckVitals(), $"{how}: acts again");
            Check.Eq(LastKit(), Kit2, $"{how}: inside the walk's reuse the used-up kit is skipped: Kit2");
            Check.True(FakeHost.Logs.Any(l => l.Contains("0x80073C1B", StringComparison.Ordinal) && l.Contains("is used up", StringComparison.Ordinal)),
                $"{how}: logged with the id");
        }
    }

    private static void UsesLeftKeepsKit()
    {
        var b = Rig(withRule: true);
        Check.True(b.CheckVitals(), "acts");
        Check.Eq(LastKit(), Kit1, "Kit1");
        ResultLine(b, $"You heal yourself for 12 Health points. Your {KitName} has 3 uses left.");
        Check.Eq(b.KitsUsedUp, 0, "uses left: nothing marked");
        Check.True(b.CheckVitals(), "acts again");
        Check.Eq(LastKit(), Kit1, "the same kit again");
    }

    // A late line from a kit that timed out can land while the next kit is in flight.
    private static void OtherKitLineIgnored()
    {
        const string Adept = "Adept Healing Kit";    // ranked ahead of Treated: the pack pick takes it
        var b = Rig(withRule: false, kit1Name: Adept);
        Check.True(b.CheckVitals(), "acts");
        Check.Eq(LastKit(), Kit1, "the Adept kit is in flight");
        ResultLine(b, $"You heal yourself for 12 Health points. Your {KitName} is used up.");
        Check.Eq(b.KitsUsedUp, 0, "the line names the Treated kit, not the one in flight: nothing marked");
        Check.True(FakeHost.Logs.Any(l => l.Contains("ignored", StringComparison.Ordinal)), "logged as ignored");
        Check.True(b.CheckVitals(), "acts again");
        Check.Eq(LastKit(), Kit1, "the Adept kit is used again");
        ResultLine(b, $"You heal yourself for 12 Health points. Your {Adept} is used up.");
        Check.Eq(b.KitsUsedUp, 1, "control: the line naming the kit in flight marks it");
    }

    private static void SkipExpires()
    {
        var b = Rig(withRule: true);
        var t0 = new DateTime(2026, 1, 1, 12, 1, 0);
        DateTime now = t0;
        b.Clock = () => now;
        b.CheckVitals();
        b.OnChatWindowText($"You heal yourself for 12 Health points. Your {KitName} is used up.", 0);
        Check.True(b.KitUsedUp(unchecked((int)Kit1)), "marked");
        now = t0.AddMilliseconds(2900);
        Check.True(b.KitUsedUp(unchecked((int)Kit1)), "2.9 s later: still skipped");
        now = t0.AddMilliseconds(3000);          // 3 s as a literal: the test must not follow the constant
        Check.False(b.KitUsedUp(unchecked((int)Kit1)), "3 s: no longer skipped (the walk has dropped a removed kit by now)");
        Check.False(b.KitUsedUp(unchecked((int)Kit2)), "another kit was never skipped");
    }

    // No GetContainerContents: the inventory is the object cache's, which keeps the removed kit. A skip
    // that ended at 3 s would pick it again for good.
    private static void NoWalkSkipLasts()
    {
        var b = Rig(withRule: true, walk: false);
        var t0 = new DateTime(2026, 1, 1, 12, 2, 0);
        DateTime now = t0;
        b.Clock = () => now;
        Check.True(b.CheckVitals(), "acts");
        Check.Eq(LastKit(), Kit1, "Kit1 first");
        Gone(Kit1);                                // the client deletes it; the cache still lists it
        ResultLine(b, $"You heal yourself for 12 Health points. Your {KitName} is used up.");
        now = t0.AddMilliseconds(800);
        Check.True(b.CheckVitals(), "acts again");
        Check.Eq(LastKit(), Kit2, "in the window: Kit2");
        ResultLine(b, $"You heal yourself for 12 Health points. Your {KitName} has 3 uses left.");
        now = t0.AddMilliseconds(3200);
        Check.True(b.CheckVitals(), "acts at +3.2 s");
        Check.Eq(LastKit(), Kit2, "past 3 s with no walk: still Kit2, never the used-up Kit1");
        Check.True(FakeHost.Logs.Any(l => l.Contains("from now on", StringComparison.Ordinal)), "logged as a lasting skip");
    }

    private static void StaminaKitUsedUp()
    {
        FakeHost.Reset();
        FakeHost.WeaponCalls = true;
        FakeHost.UseOnCalls = true;
        FakeHost.ContentsCalls = true;
        FakeHost.CombatModeValue = CombatMode.NonCombat;
        FakeHost.Names[StamKit1] = "Greater Stamina Kit";
        FakeHost.Names[StamKit2] = "Stamina Kit";
        FakeHost.Names[Gem] = "Sapphire";
        FakeHost.Containers[StamKit1] = Player;
        FakeHost.Containers[StamKit2] = Player;
        FakeHost.Containers[Gem] = Player;
        FakeHost.Skills[(Player, 6)] = (300, 2);   // Healing
        var host = FakeHost.Create(Player);
        var s = new LegacyUiSettings
        {
            IsMacroRunning = true, PeaceModeForKits = false, UseKitsInMagicMode = true,
            KitMinSuccessPct = 0, StaminaKitMinSuccessPct = 0,
        };
        var skills = new CharacterSkills(host);
        skills.SetPlayerId(Player);
        var sm = new SpellManager(host, s);
        sm.SetCharacterSkills(skills);
        var v = new PlayerVitalsCache { MaxHealth = 100, CurrentHealth = 100, MaxStamina = 1000, CurrentStamina = 100, MaxMana = 100, CurrentMana = 100 };
        var b = new BuffManager(host, s, sm, v);
        b.SetCharacterSkills(skills);
        b.SetWorldObjectCache(Frozen(FakeHost.MakeCache(host, Player, new[] { StamKit1, StamKit2, Gem })));
        var attempt = typeof(BuffManager).GetMethod("AttemptStaminaKitUse",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic, Type.EmptyTypes)!;
        Check.True((bool)attempt.Invoke(b, null)!, "the stamina kit goes out");
        Check.Eq(LastKit(), StamKit1, "the Greater one first");
        Gone(StamKit1);
        b.OnChatWindowText("You heal yourself for 300 Stamina points. Your Greater Stamina Kit is used up.", 0);
        Thread.Sleep(750);
        Check.False(b.KitPending(), "the wait ends");
        Check.True((bool)attempt.Invoke(b, null)!, "the next stamina kit goes out");
        Check.Eq(LastKit(), StamKit2, "inside the walk's reuse: the used-up Greater kit is skipped");
    }
}

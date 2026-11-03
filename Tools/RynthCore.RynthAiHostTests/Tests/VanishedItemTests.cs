using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using RynthCore.Plugin.RynthAi;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.Plugin.Shared;
using RynthCore.PluginSdk;
using RynthCore.RynthAiHostTests.Fakes;
using RynthCore.RynthAiTests.Harness;

namespace RynthCore.RynthAiHostTests.Tests;

// 2026-11-03, DreamWeave (found by the owner): a Treated Healing Kit was used up, but the cache kept it
// in the inventory (its removal was missed), so the vitals step kept "using" it: after a respawn 70
// times, every use answered at once with no heal, health rising only by natural regeneration. Kits
// and potions are now chosen from the live container walk, and the cache drops an inventory entry the
// walk doesn't find and the client can't name any more (every reader: stamina kits, tinctures, mana
// stones, component counts, meta item counts). And stamina kits have their own Stamina Kit Min Success %.
internal static class VanishedItemTests
{
    private const uint Player = 0x50000D01;
    private const uint KitA = 0x80005D01, KitB = 0x80005D02, Sword = 0x80005D03, Pack = 0x80005D04, Given = 0x80005D05, StamKit = 0x80005D06;
    private const uint STypeHealing = 6;
    private const BindingFlags Any = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

    public static void Register(Runner r)
    {
        r.Add("vanished items: a used-up kit the client no longer has leaves the cache's inventory after a live walk; wielded and still-known items stay", CacheDropsVanished);
        r.Add("vanished items: a Consumables rule's stack that is gone resolves to another carried stack of that name, never the stale one", RuleSkipsVanishedStack);
        r.Add("vanished items: without a live walk (no GetContainerContents) the cache's view is used as before", NoWalkFallsBack);
        r.Add("stamina kits: Stamina Kit Min Success % decides them, apart from Kit Min Success % (0 = always)", StaminaKitOwnLine);
    }

    private static WorldObjectCache Rig(out RynthCoreHost host, bool contents = true)
    {
        FakeHost.Reset();
        FakeHost.ContentsCalls = contents;
        FakeHost.Names[KitA] = "Treated Healing Kit";
        FakeHost.Names[KitB] = "Treated Healing Kit";
        FakeHost.Names[Sword] = "Acid Hand Wraps";
        FakeHost.Names[Given] = "Sapphire";
        FakeHost.Containers[KitA] = Player;
        FakeHost.Containers[KitB] = Player;
        FakeHost.Containers[Given] = Player;
        FakeHost.Wielders[Sword] = Player;
        host = FakeHost.Create(Player);
        return FakeHost.MakeCache(host, Player, new[] { KitA, KitB, Sword, Given });
    }

    private static bool InInventory(WorldObjectCache c, uint id) => c.GetInventory().Any(w => w.Id == unchecked((int)id));

    private static void CacheDropsVanished()
    {
        WorldObjectCache cache = Rig(out _);
        Check.True(InInventory(cache, KitA) && InInventory(cache, KitB) && InInventory(cache, Given), "both kits and the gem start in the inventory");
        cache.GetDirectInventory(forceRefresh: true);
        Check.Eq(cache.VanishedDropped, 0, "nothing dropped while everything is there");

        // KitA is used up: the client deletes it (no name, in no pack), and the delete event never reached the cache.
        FakeHost.Containers.Remove(KitA);
        FakeHost.Names.Remove(KitA);
        // The gem was given away: out of the pack, but the client still knows it (it may come back).
        FakeHost.Containers.Remove(Given);
        Thread.Sleep(300);   // past the forced walk's 250 ms floor
        cache.GetDirectInventory(forceRefresh: true);
        Check.False(InInventory(cache, KitA), "the used-up kit left the inventory");
        Check.True(InInventory(cache, KitB), "the other kit stays");
        Check.True(InInventory(cache, Given), "an item the client still knows stays (only a vanished one is dropped)");
        Check.Eq(cache.VanishedDropped, 1, "one dropped");
        Check.True(FakeHost.Logs.Any(l => l.Contains("0x80005D01 'Treated Healing Kit' is gone from the client")), "and the log says so");
        Check.True(cache.GetInventory().Count() >= 2, "the walk didn't empty the inventory");
    }

    private static void RuleSkipsVanishedStack()
    {
        WorldObjectCache cache = Rig(out _);
        var rule = new ConsumableRule { Id = unchecked((int)KitA), Name = "Treated Healing Kit", Type = "HealthKit" };
        Check.Eq(ConsumableLookup.Resolve(cache, rule)?.Id, unchecked((int)KitA), "the listed stack while it's carried");
        FakeHost.Containers.Remove(KitA);
        FakeHost.Names.Remove(KitA);
        Thread.Sleep(1100);  // past the walk's 1 s reuse
        Check.Eq(ConsumableLookup.Resolve(cache, rule)?.Id, unchecked((int)KitB), "gone: the other carried kit of that name");
        FakeHost.Containers.Remove(KitB);
        FakeHost.Names.Remove(KitB);
        Thread.Sleep(1100);
        Check.Eq(ConsumableLookup.Resolve(cache, rule), null, "none carried: nothing (the spell goes), never a stale kit");
    }

    private static void NoWalkFallsBack()
    {
        WorldObjectCache cache = Rig(out _, contents: false);
        var rule = new ConsumableRule { Id = unchecked((int)KitA), Name = "Treated Healing Kit", Type = "HealthKit" };
        Check.Eq(ConsumableLookup.Resolve(cache, rule)?.Id, unchecked((int)KitA), "no walk: the cache's listed stack, as before");
        Check.Eq(cache.VanishedDropped, 0, "and nothing is dropped without a walk");
    }

    private static void StaminaKitOwnLine()
    {
        bool Uses(int staminaPct, int healthPct)
        {
            FakeHost.Reset();
            FakeHost.WeaponCalls = true;
            FakeHost.UseOnCalls = true;
            FakeHost.CombatModeValue = CombatMode.NonCombat;
            FakeHost.ContentsCalls = true;
            FakeHost.Names[StamKit] = "Greater Stamina Kit";
            FakeHost.Containers[StamKit] = Player;
            FakeHost.Skills[(Player, STypeHealing)] = (150, 2);
            var host = FakeHost.Create(Player);
            var s = new LegacyUiSettings
            {
                IsMacroRunning = true, PeaceModeForKits = false, UseKitsInMagicMode = true,
                KitMinSuccessPct = healthPct, StaminaKitMinSuccessPct = staminaPct,
            };
            var skills = new CharacterSkills(host);
            skills.SetPlayerId(Player);
            var sm = new SpellManager(host, s);
            sm.SetCharacterSkills(skills);
            // 900 stamina missing: (150 + 200) x 1.1 = 385 against 1800: the kit would almost never work.
            var v = new PlayerVitalsCache { MaxHealth = 100, CurrentHealth = 100, MaxStamina = 1000, CurrentStamina = 100, MaxMana = 100, CurrentMana = 100 };
            var b = new BuffManager(host, s, sm, v);
            b.SetCharacterSkills(skills);
            b.SetWorldObjectCache(FakeHost.MakeCache(host, Player, new[] { StamKit }));
            ActionPacer.Reset();
            typeof(BuffManager).GetMethod("AttemptStaminaKitUse", Any)!.Invoke(b, null);
            return FakeHost.UsesOn.Any(u => u.Source == StamKit && u.Target == Player);
        }
        Check.True(new LegacyUiSettings().StaminaKitMinSuccessPct == 70, "Stamina Kit Min Success % starts at 70, like Kit Min Success %");
        Check.False(Uses(staminaPct: 70, healthPct: 0), "a near-0% stamina kit under Stamina Kit Min Success 70 is not used (Kit Min Success 0 doesn't let it)");
        Check.True(Uses(staminaPct: 0, healthPct: 90), "Stamina Kit Min Success 0: always used (Kit Min Success 90 doesn't stop it)");
    }
}

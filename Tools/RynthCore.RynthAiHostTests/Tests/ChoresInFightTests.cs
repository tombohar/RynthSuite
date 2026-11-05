using System;
using System.Linq;
using RynthCore.Plugin.RynthAi;
using RynthCore.Plugin.RynthAi.Loot;
using RynthCore.RynthAiHostTests.Fakes;
using RynthCore.RynthAiTests.Harness;

namespace RynthCore.RynthAiHostTests.Tests;

// Structural audit 2026-10-21, finding 3: item chores ignored fights. Missile crafting held combat
// with no danger check (MissileCraftingTests), mana tapping drained loot between swings
// (ManaStoneChoiceTests), and AutoCram/AutoStack moved items between swings: their only gate was
// the busy count, and a swing repeat is not a paced action. They all wait on one signal now,
// RynthAiPlugin.FightIsOn, the one salvage already used.
internal static class ChoresInFightTests
{
    private const uint Player = TickIsolationTests.Player;
    private const uint Gem = 0x80000F11, SidePack = 0x80000F12, Monster = 0x80000F13;
    private const uint ItemTypeGem = 0x800, ItemTypeContainer = 0x200;
    private const uint ItemsCapacityKey = 6;

    public static void Register(Runner r)
    {
        r.Add("chores: a fight is a monster in Monster Range or on top of us; never with the macro or combat off", FightSignal);
        r.Add("chores: AutoCram waits while a monster is on us, and crams once the fight is over", CramWaitsForFight);
        r.Add("chores: AutoCram stands down while RynthSort's auto-sort is on (they fought); off again, it crams; /ub autocram by hand still runs", CramOffWithAutoSort);
    }

    private static CombatManager.ScannedTarget At(double yards) =>
        new() { Id = unchecked((int)Monster), Distance = yards, Name = "Drudge Skulker" };

    private static void FightSignal()
    {
        try
        {
            var bot = new TickIsolationTests.Bot(combat: true, heal: false);
            bot.S.MonsterRange = 20;
            var cm = (CombatManager)bot.Get("_combatManager")!;
            Check.False(bot.P.FightIsOn(), "no monster: no fight");

            cm.SetScannedTargetsForTests(new[] { At(15) });
            Check.True(bot.P.FightIsOn(), "a monster inside Monster Range");
            cm.SetScannedTargetsForTests(new[] { At(30) });
            Check.False(bot.P.FightIsOn(), "only one past it: no fight");

            bot.S.MonsterRange = 2;
            cm.SetScannedTargetsForTests(new[] { At(4) });
            Check.True(bot.P.FightIsOn(), "Monster Range 2, a monster 4 yd away: on top of us all the same");

            bot.S.IsMacroRunning = false;
            Check.False(bot.P.FightIsOn(), "macro off: the scan's last picture holds nothing");
            bot.S.IsMacroRunning = true;
            bot.S.EnableCombat = false;
            Check.False(bot.P.FightIsOn(), "combat off: no fight");
        }
        finally { TickIsolationTests.Done(); }
    }

    private static void CramWaitsForFight()
    {
        try
        {
            var bot = new TickIsolationTests.Bot(combat: true, heal: false, arrange: () =>
            {
                FakeHost.Names[Gem] = "Black Opal";
                FakeHost.ItemTypes[Gem] = ItemTypeGem;
                FakeHost.Containers[Gem] = Player;
                FakeHost.Names[SidePack] = "Backpack";
                FakeHost.ItemTypes[SidePack] = ItemTypeContainer;
                FakeHost.Ints[(SidePack, ItemsCapacityKey)] = 24;
                FakeHost.Containers[SidePack] = Player;
            });
            _ = bot.Cache[unchecked((int)Gem)];
            _ = bot.Cache[unchecked((int)SidePack)];
            bot.S.EnableAutocram = true;
            bot.S.MonsterRange = 20;
            bot.Set("_inventoryManager", new InventoryManager(bot.Host, bot.S, bot.Cache));
            var cm = (CombatManager)bot.Get("_combatManager")!;

            cm.SetScannedTargetsForTests(new[] { At(2) });
            for (int i = 0; i < 5; i++) bot.Call("TickCramStack", bot.S);
            Check.Eq(FakeHost.Moves.Count, 0, "a monster on us: nothing is moved");

            cm.SetScannedTargetsForTests(Array.Empty<CombatManager.ScannedTarget>());
            bot.Call("TickCramStack", bot.S);
            Check.True(FakeHost.Moves.Any(m => m.Id == Gem && m.Pack == SidePack),
                "the fight is over: the gem goes into the side pack (moves: " + string.Join(",", FakeHost.Moves.Select(m => $"{m.Id:X8}->{m.Pack:X8}")) + ")");
        }
        finally { TickIsolationTests.Done(); }
    }

    // Owner, 2026-11-03: "Auto sort and auto cram are fighting each other. When auto sort is on, auto
    // cram should be disabled". RynthAi reads RynthSort's auto-sort (LegacyUiSettings.AutoSortOn).
    private static void CramOffWithAutoSort()
    {
        try
        {
            var bot = new TickIsolationTests.Bot(combat: true, heal: false, arrange: () =>
            {
                FakeHost.Names[Gem] = "Black Opal";
                FakeHost.ItemTypes[Gem] = ItemTypeGem;
                FakeHost.Containers[Gem] = Player;
                FakeHost.Names[SidePack] = "Backpack";
                FakeHost.ItemTypes[SidePack] = ItemTypeContainer;
                FakeHost.Ints[(SidePack, ItemsCapacityKey)] = 24;
                FakeHost.Containers[SidePack] = Player;
            });
            _ = bot.Cache[unchecked((int)Gem)];
            _ = bot.Cache[unchecked((int)SidePack)];
            bot.S.EnableAutocram = true;
            bot.S.AutoSortOn = true;
            var inv = new InventoryManager(bot.Host, bot.S, bot.Cache);
            bot.Set("_inventoryManager", inv);
            for (int i = 0; i < 5; i++) bot.Call("TickCramStack", bot.S);
            Check.Eq(FakeHost.Moves.Count, 0, "auto-sort on: Auto Cram moves nothing (the setting itself stays on)");
            Check.True(bot.S.EnableAutocram, "the player's Auto Cram choice is kept");

            inv.RunManual(cram: true, stack: false);
            bot.Call("TickCramStack", bot.S);
            Check.True(FakeHost.Moves.Any(m => m.Id == Gem && m.Pack == SidePack), "a cram asked for by hand still runs");

            FakeHost.Moves.Clear();
            FakeHost.Containers[Gem] = Player;   // back in the main pack
            bot.S.AutoSortOn = false;
            var inv2 = new InventoryManager(bot.Host, bot.S, bot.Cache);
            bot.Set("_inventoryManager", inv2);
            bot.Call("TickCramStack", bot.S);
            Check.True(FakeHost.Moves.Any(m => m.Id == Gem && m.Pack == SidePack), "auto-sort off: Auto Cram crams again");
        }
        finally { TickIsolationTests.Done(); }
    }
}

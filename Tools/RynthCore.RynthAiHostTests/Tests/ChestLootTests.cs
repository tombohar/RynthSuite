using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using RynthCore.Loot;
using RynthCore.Plugin.RynthAi;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.Plugin.RynthAi.Loot;
using RynthCore.PluginCore;
using RynthCore.PluginSdk;
using RynthCore.RynthAiHostTests.Fakes;
using RynthCore.RynthAiTests.Harness;

namespace RynthCore.RynthAiHostTests.Tests;

// 2026-10-12: with Looting on, a chest the player or a meta opened is looted by the corpse
// looter (RynthAiPlugin.ChestLoot.cs): matching items picked up, the rest left, no walk and no
// open, closed when done, never marked done for good (chests regenerate). Not the player's own
// packs, not house storage, nothing with Looting off.
internal static class ChestLootTests
{
    private const uint Player = 0x50000D01;
    private const uint Chest = 0x7A9B4001;     // a landblock chest (static id)
    private const uint Gem = 0x80263101;
    private const uint Spoon = 0x80263102;
    private const uint Gem2 = 0x80263103;
    private const uint Cap = 0x80263104;
    private const uint SidePack = 0x80263200;
    private const uint PackGem = 0x80263201;
    private const uint ItemTypeContainer = 0x200, ItemTypeGem = 0x800, ItemTypeMisc = 0x80, ItemTypeArmor = 0x2;
    private const BindingFlags Any = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

    public static void Register(Runner r)
    {
        r.Add("chest loot: an opened chest's matching items are picked up, the rest left; no walk, no open, closed when done", OpenedChestLooted);
        r.Add("chest loot: your own side pack opened -> nothing taken", OwnSidePackNotLooted);
        r.Add("chest loot: house storage (HouseOwner set) -> nothing taken", HouseStorageNotLooted);
        r.Add("chest loot: house storage by name (engine without the HouseOwner read) -> nothing taken", HouseStorageByNameNotLooted);
        r.Add("chest loot: Looting off -> nothing taken, the loot check still runs", LootingOffNothing);
        r.Add("chest loot: reopening the chest loots it again (never marked done)", ReopenLootsAgain);
        r.Add("chest loot: a salvage-only item is left when no UST is carried, as on corpses", SalvageWithoutUstLeft);
        r.Add("chest loot: opened mid-fight it waits for combat, then is looted if still open", ChestWaitsForCombat);
        r.Add("chest loot: closed by the player mid-loot -> session ends, chest not reopened", ClosedMidLootNotReopened);
        r.Add("chest loot: a chest gets 2 minutes (not the 12 s corpse timeout), restarted by each pickup", ChestTimeoutLongAndRestarted);
    }

    // ── World set-up ──────────────────────────────────────────────────────────

    private static (uint Cell, float X, float Y, float Z) Me0 => (0x1048002Au, 100f, 100f, 0f);

    private static void BaseWorld(string chestName = "Chest")
    {
        FakeHost.Reset();
        FakeHost.SalvageCalls = true;       // UseObject + MoveItemExternal
        FakeHost.WeaponCalls = true;        // RequestId, HasAppraisalData
        FakeHost.CloseContainerCalls = true; // CloseContainer + GetGroundContainerId
        FakeHost.Names[Player] = "Dargoth Hera";
        FakeHost.Positions[Player] = Me0;
        FakeHost.PlayerPose = Me0;

        FakeHost.Names[Chest] = chestName;
        FakeHost.ItemTypes[Chest] = ItemTypeContainer;
        FakeHost.Positions[Chest] = (Me0.Cell, Me0.X + 1.0f, Me0.Y, Me0.Z);
        FakeHost.NotAttackable.Add(Chest);
        FakeHost.Containers[Chest] = 0;     // lies in the world (ownership readable, nobody holds it)

        AddItem(Gem, "Black Opal", ItemTypeGem, Chest);
        AddItem(Spoon, "Rusty Spoon", ItemTypeMisc, Chest);
    }

    private static void AddItem(uint id, string name, uint type, uint container)
    {
        FakeHost.Names[id] = name;
        FakeHost.ItemTypes[id] = type;
        FakeHost.Containers[id] = container;
        FakeHost.Appraised.Add(id);
    }

    private sealed class Bot
    {
        public readonly RynthAiPlugin P = new();
        public readonly LegacyUiSettings S = new()
        {
            IsMacroRunning = true,
            EnableLooting = true,
            EnableCombat = false,
            LootOwnership = 0,
            LootContentSettleMs = 0,
            LootEmptyCorpseMs = 0,
            StopLootingWhenPackFull = false,
        };
        public readonly WorldObjectCache Cache;

        public Bot(RynthCoreHost host, bool salvageRule = false)
        {
            typeof(RynthPluginBase).GetMethod("Attach", Any)!.Invoke(P, new object[] { FakeHost.LastApi });
            var dash = (LegacyDashboardRenderer)RuntimeHelpers.GetUninitializedObject(typeof(LegacyDashboardRenderer));
            typeof(LegacyDashboardRenderer).GetField("_settings", Any)!.SetValue(dash, S);
            Set("_dashboard", dash);
            Cache = new WorldObjectCache(host);
            Cache.SetPlayerId(Player);
            Set("_objectCache", Cache);
            Set("_playerId", Player);

            string profile = Path.Combine(Program.TempRoot, salvageRule ? "loot-chest-salvage.json" : "loot-chest-test.json");
            var p = new LootProfile { Name = "chest" };
            var gems = new LootRule { Name = "Gems", Action = LootAction.Keep };
            gems.Conditions.Add(new ObjectClassCondition { ObjectClass = AcObjectClass.Gem });
            p.Rules.Add(gems);
            if (salvageRule)
            {
                var armor = new LootRule { Name = "Salvage armor", Action = LootAction.Salvage };
                armor.Conditions.Add(new ObjectClassCondition { ObjectClass = AcObjectClass.Armor });
                p.Rules.Add(armor);
            }
            p.Save(profile);
            S.CurrentLootPath = profile;
        }

        public void Set(string f, object? v) => typeof(RynthAiPlugin).GetField(f, Any)!.SetValue(P, v);
        public T Get<T>(string f) => (T)typeof(RynthAiPlugin).GetField(f, Any)!.GetValue(P)!;
        public object? Call(string m, params object[] a) => typeof(RynthAiPlugin).GetMethod(m, Any)!.Invoke(P, a);
        public bool HasLootWork() => (bool)Call("HasLootWork", S, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())!;
        public void Tick(BotActivity act = BotActivity.Looting)
        {
            Set("_activity", act);
            Call("TickCorpseOpening");
        }

        public void Create(params uint[] ids)
        {
            foreach (uint id in ids) Cache.OnCreateObject(id);
            Cache.Tick();
        }

        /// <summary>The server opens <paramref name="container"/> for the player (ViewContents, then the inventory update).</summary>
        public void ServerOpens(uint container)
        {
            FakeHost.GroundContainer = container;
            P.OnViewObjectContents(container);
            P.OnUpdateObjectInventory(container);
        }

        /// <summary>The server's answer to a close: the window goes.</summary>
        public void ServerCloses(uint container)
        {
            if (FakeHost.GroundContainer == container) FakeHost.GroundContainer = 0;
            P.OnStopViewingObjectContents(container);
        }

        public void TickUntil(Func<bool> done, int max = 200)
        {
            for (int i = 0; i < max && !done(); i++)
            {
                Tick();
                Thread.Sleep(10);
            }
        }

        public void TickFor(int ms, BotActivity act = BotActivity.Looting)
        {
            long end = Environment.TickCount64 + ms;
            while (Environment.TickCount64 < end)
            {
                Tick(act);
                Thread.Sleep(10);
            }
        }

        public bool Moved(uint id) => FakeHost.ExternalMoves.Any(m => m.Id == id);
        public bool Marked(uint id) => Get<Dictionary<int, long>>("_completedCorpses").ContainsKey(unchecked((int)id));
    }

    private static string Moves() => string.Join(",", FakeHost.ExternalMoves.Select(m => $"{m.Id:X8}->{m.Target:X8}"));

    /// <summary>Loots the chest's gem: picked up, lands in the pack, the chest is closed. Returns the bot.</summary>
    private static void LootGemAndFinish(Bot bot, uint gem)
    {
        bot.TickUntil(() => bot.Moved(gem));
        Check.True(FakeHost.ExternalMoves.Contains((gem, Player, 0)), $"the gem 0x{gem:X8} is moved into the main pack (moves: {Moves()})");
        int closesBefore = FakeHost.Closes.Count;
        FakeHost.Containers[gem] = Player; // it lands in the pack
        bot.TickUntil(() => FakeHost.Closes.Count > closesBefore);
    }

    // ── Tests ─────────────────────────────────────────────────────────────────

    private static void OpenedChestLooted()
    {
        BaseWorld();
        var host = FakeHost.Create(Player);
        var bot = new Bot(host);
        bot.Create(Chest, Gem, Spoon);
        Check.False(bot.HasLootWork(), "a closed chest is no loot work");

        bot.ServerOpens(Chest);
        Check.True(bot.HasLootWork(), "an open chest is loot work (the arbiter gives Looting the tick, nav stops)");

        LootGemAndFinish(bot, Gem);
        Check.False(bot.Moved(Spoon), "the spoon matches nothing and stays in the chest");
        Check.False(FakeHost.Uses.Contains(Chest), "no open sent: the chest was open already");
        Check.False(FakeHost.AutoRunCalls.Any(on => on), "no walk toward it");
        Check.Eq(string.Join(",", FakeHost.Closes.Select(c => $"{c:X8}")), $"{Chest:X8}", "closed once when done, as a finished corpse is");
        Check.False(bot.Marked(Chest), "never put in the completed-corpse set");
        Check.Eq(bot.Get<int>("_openedContainerId"), 0, "the looter lets go of it");
        Check.True(FakeHost.Logs.Any(l => l.Contains("Chest loot: looting open chest")), "the start is logged as Chest loot");
        Check.True(FakeHost.Logs.Any(l => l.Contains("Chest loot: picking up") && l.Contains($"{Gem:X8}")), "the pickup is logged as Chest loot");
        Check.True(FakeHost.Logs.Any(l => l.Contains("Chest loot: done with chest")), "the finish is logged");

        // The server's answer to the close; nothing more happens.
        bot.ServerCloses(Chest);
        bot.TickFor(100);
        Check.Eq(FakeHost.Closes.Count, 1, "still one close");
        Check.False(bot.HasLootWork(), "no loot work left");
    }

    private static void ChestTimeoutLongAndRestarted()
    {
        BaseWorld();
        var host = FakeHost.Create(Player);
        var bot = new Bot(host);
        bot.S.LootCorpseTimeoutMs = 12000;

        // A corpse keeps the setting.
        bot.Set("_targetCorpseId", 0x7A9B4002);
        Check.Eq((int)bot.Call("GetCorpseTimeoutMs", bot.S)!, 12000, "a corpse: the Corpse Timeout setting");

        // A chest: 2 minutes, or the setting when that is longer.
        bot.Set("_targetCorpseId", unchecked((int)Chest));
        bot.Set("_lootChestId", unchecked((int)Chest));
        Check.Eq((int)bot.Call("GetCorpseTimeoutMs", bot.S)!, RynthAiPlugin.ChestLootTimeoutMs, "a chest: ChestLootTimeoutMs");
        Check.Eq(RynthAiPlugin.ChestLootTimeoutMs, 120_000, "2 minutes");
        bot.S.LootCorpseTimeoutMs = 300_000;
        Check.Eq((int)bot.Call("GetCorpseTimeoutMs", bot.S)!, 300_000, "a longer Corpse Timeout still wins");
        bot.S.LootCorpseTimeoutMs = 12000;

        // 100 s into the session, a pickup: the clock restarts, so the session isn't timed out.
        long now = 1_000_000;
        bot.Set("_corpseTargetSince", now - 100_000);
        bot.Set("_currentLootItemId", unchecked((int)Gem));
        bot.Call("ConfirmCurrentLootItemMoved", now, "test");
        Check.Eq(bot.Get<long>("_corpseTargetSince"), now, "a confirmed pickup restarts the chest's clock");
        Check.False((bool)bot.Call("HasCorpseTimedOut", bot.S, now + 119_000)!, "119 s after the last pickup: still going");
        Check.True((bool)bot.Call("HasCorpseTimedOut", bot.S, now + 120_000)!, "120 s with no pickup: timed out");

        // A corpse pickup doesn't touch the corpse clock.
        bot.Set("_lootChestId", 0);
        bot.Set("_targetCorpseId", 0x7A9B4002);
        bot.Set("_corpseTargetSince", now - 5_000);
        bot.Set("_currentLootItemId", unchecked((int)Gem2));
        bot.Call("ConfirmCurrentLootItemMoved", now, "test");
        Check.Eq(bot.Get<long>("_corpseTargetSince"), now - 5_000, "a corpse keeps one timeout for the whole corpse");
    }

    private static void OwnSidePackNotLooted()
    {
        BaseWorld();
        FakeHost.Names[SidePack] = "Pack";
        FakeHost.ItemTypes[SidePack] = ItemTypeContainer;
        FakeHost.Containers[SidePack] = Player;  // in the main pack
        AddItem(PackGem, "Black Opal", ItemTypeGem, SidePack);
        var host = FakeHost.Create(Player);
        var bot = new Bot(host);
        bot.Create(SidePack, PackGem);

        bot.ServerOpens(SidePack);                // ACE sends ViewContents for side packs at login
        Check.False(bot.HasLootWork(), "your own side pack is no loot work");
        bot.TickFor(300);
        Check.Eq(FakeHost.ExternalMoves.Count, 0, "nothing moved out of your own pack");
        Check.Eq(FakeHost.Closes.Count, 0, "and it is not closed");
        Check.True(FakeHost.Logs.Any(l => l.Contains("Chest loot: not looting") && l.Contains("in your inventory")), "the reason is logged");

        // The main pack itself never counts either.
        bot.ServerOpens(Player);
        Check.False(bot.HasLootWork(), "the main pack is no loot work");
    }

    private static void HouseStorageNotLooted()
    {
        BaseWorld();
        FakeHost.InstanceIds[(Chest, 32)] = 0x50000ABC; // HouseOwner: a house-linked object of an owned house
        var host = FakeHost.Create(Player);
        Check.True(host.HasGetObjectInstanceIdProperty, "the host reads STypeIID properties");
        var bot = new Bot(host);
        bot.Create(Chest, Gem, Spoon);

        bot.ServerOpens(Chest);
        Check.False(bot.HasLootWork(), "house storage is no loot work");
        bot.TickFor(300);
        Check.Eq(FakeHost.ExternalMoves.Count, 0, "nothing taken out of house storage");
        Check.Eq(FakeHost.Closes.Count, 0, "and it is left open for the player");
        Check.True(FakeHost.Logs.Any(l => l.Contains("Chest loot: not looting") && l.Contains("house storage")), "the reason is logged");
    }

    private static void HouseStorageByNameNotLooted()
    {
        BaseWorld(chestName: "Storage");
        var host = FakeHost.Create(Player);
        Check.False(host.HasGetObjectInstanceIdProperty, "no STypeIID read here");
        var bot = new Bot(host);
        bot.Create(Chest, Gem, Spoon);

        bot.ServerOpens(Chest);
        Check.False(bot.HasLootWork(), "a container named Storage is no loot work");
        bot.TickFor(300);
        Check.Eq(FakeHost.ExternalMoves.Count, 0, "nothing taken");
        Check.True(RynthAiPlugin.IsHouseStorageName("Wall Hook"), "hooks are house objects too");
        Check.False(RynthAiPlugin.IsHouseStorageName("Ornate Chest"), "a treasure chest is not");
    }

    private static void LootingOffNothing()
    {
        BaseWorld();
        var host = FakeHost.Create(Player);
        var bot = new Bot(host);
        bot.S.EnableLooting = false;
        bot.Create(Chest, Gem, Spoon);

        bot.ServerOpens(Chest);
        Check.False(bot.HasLootWork(), "no loot work with Looting off");
        bot.TickFor(300, BotActivity.Navigating);
        Check.Eq(FakeHost.ExternalMoves.Count, 0, "nothing taken");
        Check.Eq(FakeHost.Closes.Count, 0, "and it is not closed");
        Check.False(FakeHost.Logs.Any(l => l.Contains("Chest loot:")), "no chest-loot lines at all");
        Check.True(FakeHost.Chat.Any(c => c.Contains("Black Opal") || c.Contains("Rusty Spoon") || c.Contains("loot profile")),
            "the old loot check of a natively opened container still runs (chat: " + string.Join(" | ", FakeHost.Chat) + ")");
    }

    private static void ReopenLootsAgain()
    {
        BaseWorld();
        var host = FakeHost.Create(Player);
        var bot = new Bot(host);
        bot.Create(Chest, Gem, Spoon);

        bot.ServerOpens(Chest);
        LootGemAndFinish(bot, Gem);
        bot.ServerCloses(Chest);
        Check.Eq(FakeHost.Closes.Count, 1, "the first open ends with one close");

        // The chest regenerates: a new gem; a meta uses it again.
        AddItem(Gem2, "Fire Opal", ItemTypeGem, Chest);
        bot.Create(Gem2);
        bot.ServerOpens(Chest);
        Check.True(bot.HasLootWork(), "the reopened chest is loot work again");
        LootGemAndFinish(bot, Gem2);
        Check.Eq(FakeHost.Closes.Count, 2, "and is closed again when done");
        Check.False(bot.Moved(Spoon), "the spoon is still left");
    }

    private static void SalvageWithoutUstLeft()
    {
        BaseWorld();
        AddItem(Cap, "Leather Cap", ItemTypeArmor, Chest);
        var host = FakeHost.Create(Player);
        var bot = new Bot(host, salvageRule: true);
        bot.Set("_salvageManager", new SalvageManager(host, bot.S, bot.Cache));
        bot.Create(Chest, Gem, Spoon, Cap);

        bot.ServerOpens(Chest);
        LootGemAndFinish(bot, Gem);
        Check.False(bot.Moved(Cap), "the salvage-only cap stays in the chest: no UST to salvage it with");
        Check.True(FakeHost.Logs.Any(l => l.Contains("Leather Cap") && l.Contains("no UST carried")), "logged as for corpses");
        Check.True(FakeHost.Chat.Any(c => c.Contains("No UST in your pack")), "and said once in chat");
    }

    private static void ChestWaitsForCombat()
    {
        BaseWorld();
        var host = FakeHost.Create(Player);
        var bot = new Bot(host);
        bot.Create(Chest, Gem, Spoon);
        bot.ServerOpens(Chest);
        Check.True(bot.HasLootWork(), "the chest wants the tick");

        // Combat owns the tick: nothing taken, the chest waits.
        bot.TickFor(300, BotActivity.Combat);
        Check.Eq(FakeHost.ExternalMoves.Count, 0, "nothing taken while combat holds the tick");
        Check.Eq(bot.Get<int>("_openedContainerId"), 0, "the chest isn't taken as the loot container yet");

        // The fight ends, the chest is still open: looted.
        LootGemAndFinish(bot, Gem);
        Check.Eq(FakeHost.Closes.Count, 1, "looted and closed after the fight");
    }

    private static void ClosedMidLootNotReopened()
    {
        BaseWorld();
        var host = FakeHost.Create(Player);
        var bot = new Bot(host);
        bot.Create(Chest, Gem, Spoon);
        bot.ServerOpens(Chest);
        bot.TickUntil(() => bot.Moved(Gem));
        Check.True(bot.Moved(Gem), "the gem's move is sent");

        // The player closes it before the gem arrives.
        bot.ServerCloses(Chest);
        Check.Eq(bot.Get<int>("_openedContainerId"), 0, "the session ends");
        bot.TickFor(300);
        Check.False(FakeHost.Uses.Contains(Chest), "the chest is never reopened by the looter");
        Check.Eq(FakeHost.Closes.Count, 0, "nothing to close: it is closed already");
        Check.False(bot.Marked(Chest), "and not marked: opening it again loots it");
        Check.True(FakeHost.Logs.Any(l => l.Contains("Chest loot:") && l.Contains("before looting finished")), "logged");
    }
}

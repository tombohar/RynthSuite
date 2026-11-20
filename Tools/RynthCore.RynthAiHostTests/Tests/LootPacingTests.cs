using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using RynthCore.Loot;
using RynthCore.Plugin.RynthAi;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.Plugin.RynthAi.Loot;
using RynthCore.Plugin.Shared;
using RynthCore.PluginCore;
using RynthCore.PluginSdk;
using RynthCore.RynthAiHostTests.Fakes;
using RynthCore.RynthAiTests.Harness;

namespace RynthCore.RynthAiHostTests.Tests;

// Loot findings:
//  H. A corpse use sent between the salvage UST use and the salvage execute is swallowed by the
//     client (the salvage panel open wins), and the corpse loop then waits for an open that never
//     comes. Corpse opens (and every automatic door/corpse use, via UseAudit.WorldUseHold) wait
//     while the salvage panel is pending; the corpse claim's timeout is not charged meanwhile.
//  I. R-20261117-230613-88c5 (2026-11-18), "after looting a corpse it says you can only move or use
//     one item at a time": AutoCram ran on the first tick after a corpse was released, and the
//     looter used the next corpse 1-3 ms after the cram's move. The client still held the move in
//     its one pending item request, refused the use and sent nothing, and the looter waited 5 s for
//     an answer (four times in 100 s). The open now waits for the move's answer (the item out of the
//     main pack, or the server refusing it), 3 s at most; so do the other item actions (PacedBusy).
internal static class LootPacingTests
{
    private const uint Player = 0x50000E01;
    private const uint CorpseA = 0x80069ACE;
    private const uint CorpseB = 0x80073023;
    private const uint Ust = 0x80000E11;
    private const uint Junk = 0x80000E12;
    private const uint Gem = 0x80000E13;
    private const uint SidePack = 0x80000E14;
    private const uint ItemTypeGem = 0x800;
    private const uint ItemsCapacityKey = 6;
    private const uint UstWcid = 20646;
    private const uint ItemTypeContainer = 0x200, ItemTypeArmor = 0x2;
    private const uint LongDescKey = 16;
    private const string Me = "Drakkon";
    private const BindingFlags Any = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
    private static (uint Cell, float X, float Y, float Z) Me0 => (0x6347028Au, 100f, 100f, 0f);

    public static void Register(Runner r)
    {
        r.Add("salvage/loot: no corpse open (or auto door/corpse use) while the salvage panel is pending; the claim is kept and opened after the execute", CorpseOpenWaitsForSalvagePanel);
        r.Add("loot pacing (R-20261117-230613-88c5): no corpse open (or auto door/corpse use, salvage, stone) while AutoCram's move is unanswered; the open goes on the answer", CorpseOpenWaitsForCramMove);
        r.Add("loot pacing (R-20261117-230613-88c5): a cram the server refuses frees the open at once; a lost answer holds it 3 s at most", CramMoveHoldBounds);
    }

    private static void World()
    {
        FakeHost.Reset();
        FakeHost.SalvageCalls = true;   // UseObject + MoveItemExternal + salvage panel
        FakeHost.WeaponCalls = true;
        FakeHost.ServerGates = true;
        FakeHost.LastUseDoneCalls = true;
        FakeHost.StopCalls = true;
        FakeHost.Names[Player] = Me;
        FakeHost.Positions[Player] = Me0;
        FakeHost.PlayerPose = Me0;
        foreach ((uint id, float dx) in new[] { (CorpseA, 1.0f), (CorpseB, 1.5f) })
        {
            FakeHost.Names[id] = "Corpse of Olthoi Swarm Worker";
            FakeHost.ItemTypes[id] = ItemTypeContainer;
            FakeHost.Strings[(id, LongDescKey)] = $"Killed by {Me}.";
            FakeHost.NotAttackable.Add(id);
            FakeHost.Positions[id] = (Me0.Cell, Me0.X + dx, Me0.Y, Me0.Z);
        }
        FakeHost.Names[Ust] = "Salvaging Ust";
        FakeHost.Wcids[Ust] = UstWcid;
        FakeHost.Names[Junk] = "Iron Breastplate";
        FakeHost.ItemTypes[Junk] = ItemTypeArmor;
        FakeHost.Ints[(Junk, 131)] = 64; // MaterialType
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
            EnableCombineSalvage = false,
            SalvageOpenDelayFirstMs = 400, SalvageOpenDelayFastMs = 50,
            SalvageAddDelayFirstMs = 600, SalvageAddDelayFastMs = 50,
            SalvageSalvageDelayMs = 50,
            SalvageResultDelayFirstMs = 1000, SalvageResultDelayFastMs = 250,
        };
        public readonly WorldObjectCache Cache;

        public Bot(RynthCoreHost host)
        {
            typeof(RynthPluginBase).GetMethod("Attach", Any)!.Invoke(P, new object[] { FakeHost.LastApi });
            var dash = (LegacyDashboardRenderer)RuntimeHelpers.GetUninitializedObject(typeof(LegacyDashboardRenderer));
            typeof(LegacyDashboardRenderer).GetField("_profileLoaded", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(dash, true);   // a stand-in with its profile in memory
            typeof(LegacyDashboardRenderer).GetField("_settings", Any)!.SetValue(dash, S);
            Set("_dashboard", dash);
            Cache = new WorldObjectCache(host);
            Cache.SetPlayerId(Player);
            Set("_objectCache", Cache);
            Set("_playerId", Player);
            Cache.OnCreateObject(CorpseA);
            Cache.OnCreateObject(CorpseB);
            _ = Cache[unchecked((int)Ust)];   // filed as the indexer does (FakeHost.MakeCache)
            _ = Cache[unchecked((int)Junk)];
            Cache.Tick();

            string profile = Path.Combine(Program.TempRoot, "loot-pacing-test.json");
            var p = new LootProfile { Name = "nothing" };
            p.Save(profile);
            S.CurrentLootPath = profile;
        }

        public void Set(string f, object? v) => typeof(RynthAiPlugin).GetField(f, Any)!.SetValue(P, v);
        public T Get<T>(string f) => (T)typeof(RynthAiPlugin).GetField(f, Any)!.GetValue(P)!;
        public object? Call(string m, params object[] a) => typeof(RynthAiPlugin).GetMethod(m, Any)!.Invoke(P, a);
        public void Tick()
        {
            Set("_activity", BotActivity.Looting);
            Call("TickCorpseOpening");
        }
    }

    private static int CorpseUses() => FakeHost.Uses.Count(u => u == CorpseA || u == CorpseB);

    private static void CorpseOpenWaitsForSalvagePanel()
    {
        World();
        var host = FakeHost.Create(Player);
        var bot = new Bot(host);
        long now = 60_000_000;
        var savedClock = RynthAiPlugin.CorpseClock;
        RynthAiPlugin.CorpseClock = () => now;
        try
        {
            UseAudit.Reset("RynthAi", () => true);
            // As RynthAiPlugin's init wires it.
            UseAudit.WorldUseHold = (Func<string?>)Delegate.CreateDelegate(typeof(Func<string?>), bot.P,
                typeof(RynthAiPlugin).GetMethod("SalvagePanelHold", Any)!);

            var mgr = new SalvageManager(host, bot.S, bot.Cache) { Clock = () => now };
            bot.Set("_salvageManager", mgr);
            FakeHost.OnSalvageExecute = added =>
            {
                foreach (uint id in added) { FakeHost.Names.Remove(id); bot.Cache.OnDeleteObject(id); }
            };
            mgr.EnqueueItem(Junk);

            // The salvage cycle starts: UST used, panel not executed yet.
            for (int i = 0; i < 200 && !FakeHost.Uses.Contains(Ust); i++) { now += 50; mgr.OnTick(0); }
            Check.True(FakeHost.Uses.Contains(Ust), "the UST is used");
            FakeHost.UseDone(0); // the server answers the UST use (the panel opens)
            Check.True(mgr.PanelPending, "the salvage panel is pending (UST used, no execute yet)");

            // 20 s with the panel pending (longer than the 12 s claim timeout): looting ticks on.
            for (int i = 0; i < 200; i++) { bot.Tick(); now += 100; }
            Check.Eq(CorpseUses(), 0, "no corpse use while the salvage panel is pending");
            Check.Eq(bot.Get<int>("_targetCorpseId"), unchecked((int)CorpseA), "the claim is kept, not abandoned on timeout");
            Check.Eq(FakeHost.Logs.Count(l => l.Contains("waits for the salvage panel")), 1, "the wait is logged once");

            // Any other automatic door/corpse use is held too (nav doors, the use check's probes).
            Check.False(host.UseFor(CorpseB, "Nav", "test: an automatic world use"), "an automatic world use is held");
            Check.Eq(CorpseUses(), 0, "and not sent");
            Check.True(FakeHost.Logs.Any(l => l.Contains("HELD 0x80073023")), "the hold is logged");

            // The panel executes; the corpse opens on the next looting tick.
            for (int i = 0; i < 200 && FakeHost.SalvageExecutes.Count == 0; i++) { now += 50; mgr.OnTick(0); }
            Check.Eq(FakeHost.SalvageExecutes.Count, 1, "the salvage goes out");
            Check.False(mgr.PanelPending, "the panel is no longer pending once the execute is sent");
            for (int i = 0; i < 5 && CorpseUses() == 0; i++) { bot.Tick(); now += 100; }
            Check.Eq(CorpseUses(), 1, "the corpse is opened after the execute");
            Check.True(FakeHost.Uses.Contains(CorpseA), "the claimed corpse");
        }
        finally
        {
            RynthAiPlugin.CorpseClock = savedClock;
            UseAudit.Reset("?", null);
        }
    }

    // A gem loose in the main pack and an empty side pack: AutoCram has one move to make.
    private static void CramWorld()
    {
        World();
        FakeHost.WeenieErrorCalls = true;
        FakeHost.Names[Gem] = "Black Opal";
        FakeHost.ItemTypes[Gem] = ItemTypeGem;
        FakeHost.Containers[Gem] = Player;
        FakeHost.Names[SidePack] = "Backpack";
        FakeHost.ItemTypes[SidePack] = ItemTypeContainer;
        FakeHost.Ints[(SidePack, ItemsCapacityKey)] = 24;
        FakeHost.Containers[SidePack] = Player;
    }

    private static InventoryManager CramBot(RynthCoreHost host, Bot bot)
    {
        _ = bot.Cache[unchecked((int)Gem)];
        _ = bot.Cache[unchecked((int)SidePack)];
        bot.S.EnableAutocram = true;
        var inv = new InventoryManager(host, bot.S, bot.Cache);
        bot.Set("_inventoryManager", inv);
        // As RynthAiPlugin's init wires it.
        UseAudit.WorldUseHold = (Func<string?>)Delegate.CreateDelegate(typeof(Func<string?>), bot.P,
            typeof(RynthAiPlugin).GetMethod("WorldUseHoldReason", Any)!);
        return inv;
    }

    private static int PacedBusy(Bot bot) => (int)typeof(RynthAiPlugin).GetProperty("PacedBusy", Any)!.GetValue(bot.P)!;

    private static void CorpseOpenWaitsForCramMove()
    {
        CramWorld();
        var host = FakeHost.Create(Player);
        var bot = new Bot(host);
        long now = 60_000_000;
        var start = new DateTime(2026, 11, 17, 23, 0, 0);
        var savedClock = RynthAiPlugin.CorpseClock;
        RynthAiPlugin.CorpseClock = () => now;
        RynthClock.TestNow = () => start.AddMilliseconds(now - 60_000_000);
        try
        {
            UseAudit.Reset("RynthAi", () => true);
            var inv = CramBot(host, bot);

            // The tick after a corpse is released: no corpse claimed or open, so the cram goes out.
            bot.Call("TickCramStack", bot.S);
            Check.True(FakeHost.Moves.Any(m => m.Id == Gem && m.Pack == SidePack), "AutoCram moves the gem into the side pack");
            Check.True(inv.MoveUnanswered(out string what), "the move has no answer yet");
            Check.True(what.Contains("Black Opal"), "the hold names the item: " + what);
            Check.True(PacedBusy(bot) > 0, "salvage, mana stones, pets and the next cram see it as busy");

            // The same tick and the next ones: the looter claims the next corpse, but its open waits.
            for (int i = 0; i < 5; i++) { bot.Tick(); now += 30; }
            Check.Eq(CorpseUses(), 0, "no corpse use while the cram's move is unanswered (the client would refuse it)");
            Check.Eq(bot.Get<int>("_targetCorpseId"), unchecked((int)CorpseA), "the corpse is claimed and kept");
            Check.Eq(FakeHost.Logs.Count(l => l.Contains("waits: the move of 'Black Opal'")), 1, "the wait is logged once");
            Check.False(host.UseFor(CorpseB, "Nav", "test: an automatic world use"), "an automatic door/corpse use is held too");
            Check.Eq(CorpseUses(), 0, "and not sent");

            // The server answers: the gem is in the side pack. The open goes out on the next tick.
            FakeHost.Containers[Gem] = SidePack;
            bot.Tick();
            Check.Eq(CorpseUses(), 1, "the corpse is opened once the move is answered");
            Check.True(FakeHost.Uses.Contains(CorpseA), "the claimed corpse");
            Check.False(inv.MoveUnanswered(out _), "nothing in flight");
            Check.Eq(PacedBusy(bot), 0, "and nothing busy");
        }
        finally
        {
            RynthAiPlugin.CorpseClock = savedClock;
            RynthClock.TestNow = null;
            UseAudit.Reset("?", null);
        }
    }

    private static void CramMoveHoldBounds()
    {
        CramWorld();
        var host = FakeHost.Create(Player);
        var bot = new Bot(host);
        long now = 60_000_000;
        var start = new DateTime(2026, 11, 17, 23, 0, 0);
        var savedClock = RynthAiPlugin.CorpseClock;
        RynthAiPlugin.CorpseClock = () => now;
        RynthClock.TestNow = () => start.AddMilliseconds(now - 60_000_000);
        try
        {
            UseAudit.Reset("RynthAi", () => true);
            var inv = CramBot(host, bot);

            // Refused: InventoryServerSaveFailed naming the gem (the report's last cram, 18:05:38.869).
            bot.Call("TickCramStack", bot.S);
            Check.True(inv.MoveUnanswered(out _), "the move is out");
            FakeHost.WeenieError(0, evt: 0x00A0, obj: SidePack);
            Check.True(inv.MoveUnanswered(out _), "a refusal naming another object is not this move's answer");
            FakeHost.WeenieError(0, evt: 0x00A0, obj: Gem);
            Check.False(inv.MoveUnanswered(out _), "the server refused the move: that is its answer");
            bot.Tick();
            Check.Eq(CorpseUses(), 1, "the open goes out at once");

            // Lost: no answer at all (a fresh manager, the gem still in the main pack). The hold
            // ends after MoveAnswerMaxMs. (The looter lets go of the corpse it opened, so the cram runs.)
            bot.Set("_targetCorpseId", 0);
            bot.Set("_openedContainerId", 0);
            var inv2 = CramBot(host, bot);
            bot.Call("TickCramStack", bot.S);
            Check.True(inv2.MoveUnanswered(out _), "a second move is out");
            Check.False(host.UseFor(CorpseB, "Nav", "test: an automatic world use"), "an automatic world use is held");
            long sent = now;
            bool freed = false;
            while (now - sent < 5000)
            {
                if (host.UseFor(CorpseB, "Nav", "test: an automatic world use")) { freed = true; break; }
                now += 100;
            }
            long held = now - sent;
            Check.True(freed, "the use goes out in the end");
            Check.True(held >= (long)InventoryManager.MoveAnswerMaxMs, $"held until the answer limit ({held} ms)");
            Check.True(held <= (long)InventoryManager.MoveAnswerMaxMs + 100, $"and no longer ({held} ms)");
        }
        finally
        {
            RynthAiPlugin.CorpseClock = savedClock;
            RynthClock.TestNow = null;
            UseAudit.Reset("?", null);
        }
    }
}

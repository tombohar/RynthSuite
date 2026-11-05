using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using RynthCore.Loot;
using RynthCore.Loot.VTank;
using RynthCore.Plugin.RynthAi;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.Plugin.Shared;
using RynthCore.PluginCore;
using RynthCore.PluginSdk;
using RynthCore.RynthAiHostTests.Fakes;
using RynthCore.RynthAiTests.Harness;

namespace RynthCore.RynthAiHostTests.Tests;

// 2026-10-31, the corpse looter with LootSnob (Fixtures\lootsnob-excerpt.utl plus a Salvage rule
// on gold alone): a crossbow whose ID is late is held, asked for again, and in the end left on the
// corpse; it is never judged without its ID, where its stats read 0 and the gold rule took it
// ("best-effort" after the wait, until today). One whose ID comes in time is kept by its rule.
// The loot clock (CorpseClock) is moved by hand.
internal static class LootLateIdTests
{
    private const uint Player = 0x50000D41;
    private const uint Corpse = 0x80264100;
    private const uint Xbow = 0x80264101;
    private const uint ItemTypeContainer = 0x200, ItemTypeMissile = 0x100;
    private const uint LongDescKey = 16;
    private const string Me = "Ilsa Corvane";
    private const BindingFlags Any = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
    private static (uint Cell, float X, float Y, float Z) Me0 => (0x1048002Au, 100f, 100f, 0f);

    public static void Register(Runner r)
    {
        r.Add("loot late ID: a crossbow whose ID never comes is held, asked again, then left - never the Salvage rule", IdNeverComes);
        r.Add("loot late ID: a crossbow whose ID comes after the wait is kept by its LootSnob rule", IdComesLate);
    }

    private static long _now;

    private static void Clocked(Action body)
    {
        var old = RynthAiPlugin.CorpseClock;
        _now = 60_000_000;
        RynthAiPlugin.CorpseClock = () => _now;
        try { body(); }
        finally { RynthAiPlugin.CorpseClock = old; }
    }

    private static void World()
    {
        FakeHost.Reset();
        FakeHost.SalvageCalls = true;          // UseObject + MoveItemExternal
        FakeHost.WeaponCalls = true;           // RequestId, HasAppraisalData
        FakeHost.Names[Player] = Me;
        FakeHost.Positions[Player] = Me0;
        FakeHost.PlayerPose = Me0;
        FakeHost.Names[Corpse] = "Corpse of Olthoi Swarm Drone";
        FakeHost.ItemTypes[Corpse] = ItemTypeContainer;
        FakeHost.Strings[(Corpse, LongDescKey)] = $"Killed by {Me}.";
        FakeHost.NotAttackable.Add(Corpse);
        FakeHost.Positions[Corpse] = (Me0.Cell, Me0.X + 1.0f, Me0.Y, Me0.Z);
        // What the CreateObject shows: name, type, slot, value, burden, material (gold).
        FakeHost.Names[Xbow] = "Blunt Crossbow";
        FakeHost.ItemTypes[Xbow] = ItemTypeMissile;
        FakeHost.Containers[Xbow] = Corpse;
        foreach (var (k, v) in new (uint, int)[] { (1, 0x100), (9, 0x00400000), (19, 12000), (5, 600), (131, 60) })
            FakeHost.Ints[(Xbow, k)] = v;
    }

    /// <summary>The ID lands: workmanship, weapon type, wield, +22, damage mod 2.62, Legendary Blood Thirst.</summary>
    private static void Identify()
    {
        foreach (var (k, v) in new (uint, int)[] { (105, 6), (353, 9), (158, 2), (159, 47), (160, 385), (204, 22), (45, 4), (48, 47) })
            FakeHost.Ints[(Xbow, k)] = v;
        FakeHost.Doubles[(Xbow, 63)] = 2.62;
        FakeHost.Doubles[(Xbow, 29)] = 1.15;
        FakeHost.SpellIds[Xbow] = new[] { (uint)SpellDatabase.GetIdByName("Legendary Blood Thirst") };
        FakeHost.Appraised.Add(Xbow);
    }

    private sealed class Bot
    {
        public readonly RynthAiPlugin P = new();
        public readonly LegacyUiSettings S = new()
        {
            IsMacroRunning = true, EnableLooting = true, EnableCombat = false, LootOwnership = 0,
            LootContentSettleMs = 0, LootEmptyCorpseMs = 0, StopLootingWhenPackFull = false,
            LootAssessWindowMs = 200, LootCorpseTimeoutMs = 12_000,
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
            Set("_vitals", new PlayerVitalsCache { MaxHealth = 100, CurrentHealth = 100, MaxStamina = 100, CurrentStamina = 100, MaxMana = 100, CurrentMana = 100 });

            // LootSnob's excerpt and, last, a Salvage rule on gold alone (no workmanship), so the
            // crossbow judged without its ID would be salvaged.
            VTankLootProfile p = VTankLootParser.Load(Path.Combine(AppContext.BaseDirectory, "Fixtures", "lootsnob-excerpt.utl"));
            var gold = new VTankLootRule { Name = "Gold any", Action = VTankLootAction.Salvage };
            gold.Conditions.Add(new VTankLootCondition(VTankNodeTypes.LongValKeyE, "0", new[] { "60", "131" }));
            p.Rules.Add(gold);
            string path = Path.Combine(Program.TempRoot, "loot-late-id.utl");
            File.WriteAllText(path, VTankLootWriter.Serialize(p));
            S.CurrentLootPath = path;
        }

        public void Set(string f, object? v) => typeof(RynthAiPlugin).GetField(f, Any)!.SetValue(P, v);
        public T Get<T>(string f) => (T)typeof(RynthAiPlugin).GetField(f, Any)!.GetValue(P)!;
        public void Tick()
        {
            Set("_activity", BotActivity.Looting);
            typeof(RynthAiPlugin).GetMethod("TickCorpseOpening", Any)!.Invoke(P, null);
        }
        public void TickFor(long ms)
        {
            long end = _now + ms;
            while (_now < end) { _now += 20; Tick(); }
        }
        public bool Completed(uint corpse) =>
            Get<Dictionary<int, long>>("_completedCorpses").ContainsKey(unchecked((int)corpse));
    }

    private static bool Logged(string part) => FakeHost.Logs.Any(l => l.Contains(part, StringComparison.Ordinal));
    private static bool Moved() => FakeHost.ExternalMoves.Any(m => m.Id == Xbow);

    private static Bot OpenCorpse()
    {
        World();
        var host = FakeHost.Create(Player);
        var bot = new Bot(host);
        bot.Cache.OnCreateObject(Corpse);
        bot.Cache.OnCreateObject(Xbow);
        bot.Cache.Tick();
        bot.Tick();
        Check.True(FakeHost.Uses.Contains(Corpse), "the corpse is opened");
        FakeHost.UseDone(0);
        bot.P.OnViewObjectContents(Corpse);
        bot.TickFor(100);
        Check.True(FakeHost.IdRequests.Contains(Xbow), "the crossbow's ID is asked for (a LootSnob rule that could take it needs it)");
        return bot;
    }

    private static void IdNeverComes() => Clocked(() =>
    {
        var bot = OpenCorpse();
        bot.TickFor(4_000);   // past the assess window and AppraisalWaitMs
        Check.False(Moved(), "not taken without its ID (the old best-effort took it for Gold salvage)");
        Check.True(FakeHost.IdRequests.Count(id => id == Xbow) >= 2, "its ID is asked for again");
        Check.True(Logged("no ID yet and a rule that decides it needs one"), "and that is logged");

        for (int i = 0; i < 1000 && !bot.Completed(Corpse); i++) { _now += 20; bot.Tick(); }
        Check.False(Moved(), "still not taken when the corpse is given up");
        Check.True(Logged("left: no ID came, and a rule that decides it needs one"), "left on the corpse, and said why");
        Check.True(bot.Completed(Corpse), "the corpse is finished");
        Check.False(FakeHost.Chat.Any(c => c.Contains("Gold any")), "the Salvage rule never named");
    });

    private static void IdComesLate() => Clocked(() =>
    {
        var bot = OpenCorpse();
        bot.TickFor(5_000);   // later than the old wait gave it
        Check.False(Moved(), "held while the ID is out");
        Identify();
        for (int i = 0; i < 200 && !Moved(); i++) { _now += 20; bot.Tick(); }
        Check.True(Moved(), "taken once the ID is in");
        Check.True(FakeHost.Chat.Any(c => c.Contains("Loot rule: Blunt Crossbow -> [Keep] (M) Xbow (OD +9)")),
            "by LootSnob's Xbow (OD +9) (chat: " + string.Join(" | ", FakeHost.Chat.Where(c => c.Contains("Loot rule"))) + ")");
    });
}

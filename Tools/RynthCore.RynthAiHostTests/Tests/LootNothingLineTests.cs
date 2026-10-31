using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using RynthCore.Loot;
using RynthCore.Plugin.RynthAi;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.PluginCore;
using RynthCore.PluginSdk;
using RynthCore.RynthAiHostTests.Fakes;
using RynthCore.RynthAiTests.Harness;

namespace RynthCore.RynthAiHostTests.Tests;

// Owner, 2026-10-31: a corpse or chest whose only matching items were Keep # (or mana-stone)
// items already at their count ended with just "Keep # '<rule>': have N/N — leaving <item>." and
// never a "nothing to loot" line: "It should report nothing to loot as well." The end-of-container
// line now counts confirmed pickups, not matches (NothingLootedLine).
// The loot clock (CorpseOpenController.CorpseClock) is moved by hand; the server answers at once.
internal static class LootNothingLineTests
{
    private const uint Player = 0x50000D61;
    private const uint Corpse = 0x80266100;
    private const uint Chest = 0x7A9B6001;      // a landblock chest (static id)
    private const uint Gem = 0x80266101;
    private const uint PackGem = 0x80266102;    // one already in the pack: the Keep # 1 is met
    private const uint Stone = 0x80266103, PackStone = 0x80266104;
    private const uint ItemTypeContainer = 0x200, ItemTypeGem = 0x800, ItemTypeManaStone = 0x80000;
    private const uint LongDescKey = 16;
    private const string Me = "Test Hunter";
    private const BindingFlags Any = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
    private const long TickMs = 25;
    private const string Enough = "item(s) match, but you already have enough.";
    private static (uint Cell, float X, float Y, float Z) Me0 => (0x1048002Au, 100f, 100f, 0f);

    public static void Register(Runner r)
    {
        r.Add("nothing to loot: a corpse whose only match is a Keep # at its count ends with 'you already have enough'", CappedCorpseSaysNothingToLoot);
        r.Add("nothing to loot: a corpse with a looted item says no 'nothing to loot'", LootedCorpseSaysNothing);
        r.Add("nothing to loot: a chest whose only match is a Keep # at its count ends with 'you already have enough'", CappedChestSaysNothingToLoot);
        r.Add("nothing to loot: a corpse with no matching item keeps 'matches the loot profile'", NoMatchKeepsOldLine);
        r.Add("nothing to loot: a corpse whose only match is a mana stone at its keep count ends with 'you already have enough'", CappedManaStoneSaysNothingToLoot);
    }

    // ── Clock and world ───────────────────────────────────────────────────────

    private static long _now;

    private static void Clocked(Action body)
    {
        var old = RynthAiPlugin.CorpseClock;
        _now = 90_000_000;
        RynthAiPlugin.CorpseClock = () => _now;
        try { body(); }
        finally { RynthAiPlugin.CorpseClock = old; }
    }

    private static void World(bool chest, bool gemInPack)
    {
        FakeHost.Reset();
        FakeHost.SalvageCalls = true;        // UseObject + MoveItemExternal
        FakeHost.WeaponCalls = true;         // RequestId, HasAppraisalData
        FakeHost.CloseContainerCalls = true; // CloseContainer + GetGroundContainerId
        FakeHost.ContentsCalls = true;       // GetContainerContents: the Keep # count walks the pack
        FakeHost.Names[Player] = Me;
        FakeHost.Positions[Player] = Me0;
        FakeHost.PlayerPose = Me0;
        uint box = chest ? Chest : Corpse;
        FakeHost.Names[box] = chest ? "Chest" : "Corpse of Brier Wasp";
        FakeHost.ItemTypes[box] = ItemTypeContainer;
        FakeHost.Positions[box] = (Me0.Cell, Me0.X + 1.0f, Me0.Y, Me0.Z);
        FakeHost.NotAttackable.Add(box);
        if (chest) FakeHost.Containers[Chest] = 0;   // lies in the world
        else FakeHost.Strings[(Corpse, LongDescKey)] = $"Killed by {Me}.";
        AddGem(Gem, "Black Opal", box);
        if (gemInPack) AddGem(PackGem, "Black Opal", Player);
    }

    private static void AddGem(uint id, string name, uint container)
    {
        FakeHost.Names[id] = name;
        FakeHost.ItemTypes[id] = ItemTypeGem;
        FakeHost.Containers[id] = container;
        FakeHost.Appraised.Add(id);
    }

    private sealed class Bot
    {
        public readonly RynthAiPlugin P = new();
        public readonly LegacyUiSettings S = new()
        {
            IsMacroRunning = true, EnableLooting = true, EnableCombat = false, LootOwnership = 0,
            LootContentSettleMs = 0, LootEmptyCorpseMs = 0, StopLootingWhenPackFull = false,
        };
        public readonly WorldObjectCache Cache;
        public readonly RynthCoreHost Host;
        private int _seenMoves;

        /// <param name="keepOne">Gems are Keep # 1; otherwise the profile wants only armor.</param>
        public Bot(RynthCoreHost host, bool keepOne)
        {
            Host = host;
            typeof(RynthPluginBase).GetMethod("Attach", Any)!.Invoke(P, new object[] { FakeHost.LastApi });
            var dash = (LegacyDashboardRenderer)RuntimeHelpers.GetUninitializedObject(typeof(LegacyDashboardRenderer));
            typeof(LegacyDashboardRenderer).GetField("_settings", Any)!.SetValue(dash, S);
            Set("_dashboard", dash);
            Cache = new WorldObjectCache(host);
            Cache.SetPlayerId(Player);
            Set("_objectCache", Cache);
            Set("_playerId", Player);
            Set("_vitals", new PlayerVitalsCache { MaxHealth = 100, CurrentHealth = 100, MaxStamina = 100, CurrentStamina = 100, MaxMana = 100, CurrentMana = 100 });

            string profile = Path.Combine(Program.TempRoot, keepOne ? "loot-nothing-keep1.json" : "loot-nothing-armor.json");
            var p = new LootProfile { Name = "nothing-line" };
            var rule = keepOne
                ? new LootRule { Name = "Gems", Action = LootAction.KeepUpTo, KeepCount = 1 }
                : new LootRule { Name = "Armor", Action = LootAction.Keep };
            rule.Conditions.Add(new ObjectClassCondition { ObjectClass = keepOne ? AcObjectClass.Gem : AcObjectClass.Armor });
            p.Rules.Add(rule);
            p.Save(profile);
            S.CurrentLootPath = profile;
        }

        public void Set(string f, object? v) => typeof(RynthAiPlugin).GetField(f, Any)!.SetValue(P, v);
        public T Get<T>(string f) => (T)typeof(RynthAiPlugin).GetField(f, Any)!.GetValue(P)!;
        public object? Call(string m, params object[] a) => typeof(RynthAiPlugin).GetMethod(m, Any)!.Invoke(P, a);

        /// <summary>One Looting tick; the server lands each move in the pack at once.</summary>
        public void Tick()
        {
            _now += TickMs;
            Set("_activity", BotActivity.Looting);
            Call("TickCorpseOpening");
            while (_seenMoves < FakeHost.ExternalMoves.Count)
            {
                var m = FakeHost.ExternalMoves[_seenMoves++];
                FakeHost.Containers[m.Id] = m.Target;
            }
        }

        public void Create(params uint[] ids)
        {
            foreach (uint id in ids) Cache.OnCreateObject(id);
            for (int i = 0; i < 5; i++) Cache.Tick();
        }

        public bool CorpseDone => Get<Dictionary<int, long>>("_completedCorpses").ContainsKey(unchecked((int)Corpse));
    }

    private static bool Said(string part) => FakeHost.Chat.Any(l => l.Contains(part, StringComparison.Ordinal));
    private static string ChatDump() => string.Join(" | ", FakeHost.Chat);

    /// <summary>Claims, opens (the server's ViewContents) and loots the corpse to its end.</summary>
    private static Bot LootCorpse(bool keepOne, bool gemInPack, Action<Bot>? setUp = null, params uint[] extra)
    {
        World(chest: false, gemInPack);
        foreach (uint id in extra)
        {
            FakeHost.Names[id] = "Mana Stone";
            FakeHost.ItemTypes[id] = ItemTypeManaStone;
            FakeHost.Containers[id] = id == PackStone ? Player : Corpse;
            FakeHost.Appraised.Add(id);
        }
        var host = FakeHost.Create(Player);
        var bot = new Bot(host, keepOne);
        setUp?.Invoke(bot);
        var ids = new List<uint> { Corpse, Gem };
        if (gemInPack) ids.Add(PackGem);
        ids.AddRange(extra);
        bot.Create(ids.ToArray());
        long end = _now + 30_000;
        bool opened = false;
        while (_now < end && !bot.CorpseDone)
        {
            bot.Tick();
            if (!opened && FakeHost.Uses.Contains(Corpse))
            {
                opened = true;
                FakeHost.UseDone(0);
                FakeHost.GroundContainer = Corpse;
                bot.P.OnViewObjectContents(Corpse);
                bot.P.OnUpdateObjectInventory(Corpse);
            }
        }
        Check.True(opened, "the corpse is opened");
        Check.True(bot.CorpseDone, "the corpse is finished");
        return bot;
    }

    // ── Tests ─────────────────────────────────────────────────────────────────

    private static void CappedCorpseSaysNothingToLoot() => Clocked(() =>
    {
        LootCorpse(keepOne: true, gemInPack: true);
        Check.True(Said("Keep # 'Gems': have 1/1"), $"the Keep # leave is said as before ({ChatDump()})");
        Check.False(FakeHost.ExternalMoves.Any(m => m.Id == Gem), "the gem is left");
        Check.True(Said("[RynthAi] Nothing to loot on Corpse of Brier Wasp: 1 " + Enough), $"and the end says there was nothing to loot ({ChatDump()})");
        Check.False(Said("matches the loot profile"), "not 'nothing matches': the gem did match");
    });

    private static void LootedCorpseSaysNothing() => Clocked(() =>
    {
        LootCorpse(keepOne: true, gemInPack: false);
        Check.True(FakeHost.ExternalMoves.Any(m => m.Id == Gem && m.Target == Player), "the gem is looted (none in the pack yet)");
        Check.True(Said("Looted [KeepUpTo] Black Opal"), $"and said ({ChatDump()})");
        Check.False(Said("Nothing to loot"), "no 'nothing to loot' line");
        Check.False(Said("matches the loot profile"), "nor 'nothing matches'");
    });

    private static void CappedChestSaysNothingToLoot() => Clocked(() =>
    {
        World(chest: true, gemInPack: true);
        var host = FakeHost.Create(Player);
        var bot = new Bot(host, keepOne: true);
        bot.Create(Chest, Gem, PackGem);
        FakeHost.GroundContainer = Chest;   // the player opened it
        bot.P.OnViewObjectContents(Chest);
        bot.P.OnUpdateObjectInventory(Chest);
        long end = _now + 30_000;
        while (_now < end && !FakeHost.Closes.Contains(Chest)) bot.Tick();
        Check.True(FakeHost.Logs.Any(l => l.Contains("Chest loot: done with chest")), "the chest session ends");
        Check.False(FakeHost.ExternalMoves.Any(m => m.Id == Gem), "the gem is left");
        Check.True(Said("[RynthAi] Nothing to loot on Chest: 1 " + Enough), $"the end says there was nothing to loot ({ChatDump()})");
    });

    private static void NoMatchKeepsOldLine() => Clocked(() =>
    {
        LootCorpse(keepOne: false, gemInPack: false);
        Check.True(Said("[RynthAi] Nothing on Corpse of Brier Wasp matches the loot profile."), $"the old line ({ChatDump()})");
        Check.False(Said("Nothing to loot"), "and no second line");
    });

    private static void CappedManaStoneSaysNothingToLoot() => Clocked(() =>
    {
        // The profile wants armor only; the stone is the built-in mana-stone rule (tapping on, keep 1),
        // which never counted as an evaluated item (alone on a corpse it ended with no line at all).
        LootCorpse(keepOne: false, gemInPack: false, bot =>
        {
            bot.S.EnableManaTapping = true;
            bot.S.ManaStoneKeepCount = 1;
            bot.Set("_manaStoneManager", new ManaStoneManager(bot.Host, bot.S, bot.Cache));
        }, Stone, PackStone);
        Check.True(Said("Mana stone match: 'Mana Stone' (have 1/1)"), $"the stone is left at its keep count ({ChatDump()})");
        Check.False(FakeHost.ExternalMoves.Any(m => m.Id == Stone), "the stone stays on the corpse");
        Check.True(Said("[RynthAi] Nothing to loot on Corpse of Brier Wasp: 1 " + Enough), $"the end says there was nothing to loot ({ChatDump()})");
    });
}

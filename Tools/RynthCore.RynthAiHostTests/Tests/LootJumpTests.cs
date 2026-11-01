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

// Jump-loot: the jump is the keyboard one (TapJump, 0xF61B Jump, which ACE handles), never
// JumpNonAutonomous (0xF7C9, no handler on ACE). It goes BEFORE the move, on every pickup (a
// corpse's only match included), only with the setting on and stamina above 5.
// The loot clock (RynthAiPlugin.CorpseClock) is moved by hand here.
internal static class LootJumpTests
{
    private const uint Player = 0x50000F31;
    private const uint CorpseA = 0x80263100;
    private const uint GemA = 0x80263101;
    private const uint GemA2 = 0x80263102;
    private const uint ItemTypeContainer = 0x200, ItemTypeGem = 0x800;
    private const uint LongDescKey = 16;
    private const string Me = "Test Looter";
    private const BindingFlags Any = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
    private static (uint Cell, float X, float Y, float Z) Me0 => (0x1048002Au, 100f, 100f, 0f);

    public static void Register(Runner r)
    {
        r.Add("jump loot: two-item corpse - the keyboard jump (0xF61B) goes out before the move; never the unhandled 0xF7C9", TwoItemJumpsBeforeMove);
        r.Add("jump loot: a corpse's only match jumps too (one tap jump)", SingleItemJumps);
        r.Add("jump loot: the setting off sends no jump", SettingOffNoJump);
        r.Add("jump loot: stamina of 5 or less sends no jump", LowStaminaNoJump);
    }

    private static long _now;

    private static void Clocked(Action body)
    {
        var old = RynthAiPlugin.CorpseClock;
        _now = 50_000_000;
        RynthAiPlugin.CorpseClock = () => _now;
        try { body(); }
        finally { RynthAiPlugin.CorpseClock = old; }
    }

    private static void Gem(uint id)
    {
        FakeHost.Names[id] = "Black Opal";
        FakeHost.ItemTypes[id] = ItemTypeGem;
        FakeHost.Containers[id] = CorpseA;
        FakeHost.Appraised.Add(id);
    }

    private static void World(bool secondGem)
    {
        FakeHost.Reset();
        FakeHost.SalvageCalls = true;          // UseObject + MoveItemExternal
        FakeHost.WeaponCalls = true;           // RequestId, HasAppraisalData
        FakeHost.CloseContainerCalls = true;   // CloseContainer + GetGroundContainerId
        FakeHost.TapJumpCalls = true;
        FakeHost.Names[Player] = Me;
        FakeHost.Positions[Player] = Me0;
        FakeHost.PlayerPose = Me0;
        FakeHost.Names[CorpseA] = "Corpse of Olthoi Swarm Drone";
        FakeHost.ItemTypes[CorpseA] = ItemTypeContainer;
        FakeHost.Strings[(CorpseA, LongDescKey)] = $"Killed by {Me}.";
        FakeHost.NotAttackable.Add(CorpseA);
        FakeHost.Positions[CorpseA] = (Me0.Cell, Me0.X + 1.0f, Me0.Y, Me0.Z);
        Gem(GemA);
        if (secondGem) Gem(GemA2);
    }

    /// <summary>Opens the corpse and ticks until the first pickup's move has gone out.</summary>
    private static void LootUntilFirstMove(bool jumpEnabled, bool secondGem, uint stamina)
    {
        World(secondGem);
        var host = FakeHost.Create(Player);
        var bot = new RynthAiPlugin();
        var s = new LegacyUiSettings
        {
            IsMacroRunning = true, EnableLooting = true, EnableCombat = false, LootOwnership = 0,
            LootContentSettleMs = 0, LootEmptyCorpseMs = 0, StopLootingWhenPackFull = false,
            LootJumpEnabled = jumpEnabled,
        };
        typeof(RynthPluginBase).GetMethod("Attach", Any)!.Invoke(bot, new object[] { FakeHost.LastApi });
        var dash = (LegacyDashboardRenderer)RuntimeHelpers.GetUninitializedObject(typeof(LegacyDashboardRenderer));
        typeof(LegacyDashboardRenderer).GetField("_settings", Any)!.SetValue(dash, s);
        void Set(string f, object? v) => typeof(RynthAiPlugin).GetField(f, Any)!.SetValue(bot, v);
        Set("_dashboard", dash);
        var cache = new WorldObjectCache(host);
        cache.SetPlayerId(Player);
        Set("_objectCache", cache);
        Set("_playerId", Player);
        Set("_vitals", new PlayerVitalsCache { MaxHealth = 100, CurrentHealth = 100, MaxStamina = 100, CurrentStamina = stamina, MaxMana = 100, CurrentMana = 100 });

        string profile = Path.Combine(Program.TempRoot, "loot-jump-test.json");
        var p = new LootProfile { Name = "gems" };
        var rule = new LootRule { Name = "Gems", Action = LootAction.Keep };
        rule.Conditions.Add(new ObjectClassCondition { ObjectClass = AcObjectClass.Gem });
        p.Rules.Add(rule);
        p.Save(profile);
        s.CurrentLootPath = profile;

        void Tick()
        {
            Set("_activity", BotActivity.Looting);
            typeof(RynthAiPlugin).GetMethod("TickCorpseOpening", Any)!.Invoke(bot, null);
        }
        foreach (uint id in secondGem ? new[] { CorpseA, GemA, GemA2 } : new[] { CorpseA, GemA }) cache.OnCreateObject(id);
        cache.Tick();
        Tick();
        Check.True(FakeHost.Uses.Contains(CorpseA), "the corpse is opened");
        FakeHost.UseDone(0);
        FakeHost.GroundContainer = CorpseA;
        bot.OnViewObjectContents(CorpseA);
        for (int i = 0; i < 200 && FakeHost.ExternalMoves.Count == 0; i++) { _now += 20; Tick(); }
        Check.True(FakeHost.ExternalMoves.Count > 0, "a gem's move is sent");
    }

    private static string Order() => string.Join(",", FakeHost.Order);

    private static void TwoItemJumpsBeforeMove() => Clocked(() =>
    {
        LootUntilFirstMove(jumpEnabled: true, secondGem: true, stamina: 100u);
        Check.Eq(FakeHost.Order.FirstOrDefault() ?? "", "tapjump", "the keyboard jump goes first: " + Order());
        Check.True(FakeHost.Order.Count >= 2 && FakeHost.Order[1].StartsWith("move:", StringComparison.Ordinal), "then the move: " + Order());
        Check.Eq(FakeHost.Jumps, 0, "never JumpNonAutonomous (0xF7C9, no handler on ACE)");
    });

    private static void SingleItemJumps() => Clocked(() =>
    {
        LootUntilFirstMove(jumpEnabled: true, secondGem: false, stamina: 100u);
        Check.Eq(FakeHost.Order.FirstOrDefault() ?? "", "tapjump", "single item: the jump goes first: " + Order());
        Check.True(FakeHost.Order.Count >= 2 && FakeHost.Order[1].StartsWith("move:", StringComparison.Ordinal), "single item: then the move: " + Order());
        Check.Eq(FakeHost.TapJumps, 1, "single item: exactly one tap jump");
        Check.Eq(FakeHost.Jumps, 0, "single item: never JumpNonAutonomous");
    });

    private static void SettingOffNoJump() => Clocked(() =>
    {
        LootUntilFirstMove(jumpEnabled: false, secondGem: true, stamina: 100u);
        Check.Eq(FakeHost.TapJumps + FakeHost.Jumps, 0, "jump-loot off: no jump: " + Order());
    });

    private static void LowStaminaNoJump() => Clocked(() =>
    {
        LootUntilFirstMove(jumpEnabled: true, secondGem: true, stamina: 5u);
        Check.Eq(FakeHost.TapJumps + FakeHost.Jumps, 0, "stamina 5: no jump: " + Order());
    });
}

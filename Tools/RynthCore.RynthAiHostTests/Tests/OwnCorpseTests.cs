using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using RynthCore.Loot;
using RynthCore.Plugin.RynthAi;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.PluginCore;
using RynthCore.PluginSdk;
using RynthCore.RynthAiHostTests.Fakes;
using RynthCore.RynthAiTests.Harness;

namespace RynthCore.RynthAiHostTests.Tests;

// 2026-10-05, owner: "It needs to loot your corpse and every item on it. If it's a setting default
// to true please." Recover My Corpse (LootOwnCorpse, default on) takes every item on your own death
// corpse whatever the loot profile and Loot From say; the death spot is remembered; Travel Back To
// My Corpse (TravelToOwnCorpse, default off) walks back with RynthNav (/rnav go via the broker).
internal static class OwnCorpseTests
{
    private const uint Player = 0x50000D01;
    private const uint Corpse = 0x80261100;
    private const uint Gem = 0x80261101;
    private const uint Cloak = 0x80261102;
    private const uint Coins = 0x80261103;
    private const uint ItemTypeClothing = 0x4, ItemTypeMoney = 0x40, ItemTypeContainer = 0x200, ItemTypeGem = 0x800;
    private const uint LongDescKey = 16;
    private const string Me = "Dargoth Hera";
    private const BindingFlags Any = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

    public static void Register(Runner r)
    {
        r.Add("own corpse: recognised by exact name + 'Killed by' line, never a namesake or another player's", Identification);
        r.Add("own corpse: every item is taken, the loot profile and Loot From ignored, then closed", RecoveredWhole);
        r.Add("own corpse: another player's corpse with a similar name is left alone", SimilarNameUntouched);
        r.Add("own corpse: Recover My Corpse off = the behaviour before (profile only, My Kills skips it)", SettingOffIsOldBehaviour);
        r.Add("own corpse: beats a nearer corpse of your kill; a timeout retries it instead of writing it off", PriorityAndTimeout);
        r.Add("own corpse: settings default on/off, an old profile loads on, saved and bridged", SettingsDefaults);
        r.Add("own corpse: the death spot is remembered and said, server lines read", DeathSpotRemembered);
        r.Add("own corpse: travel back walks with RynthNav, stops for combat, ends in loot range", TravelBack);
        r.Add("own corpse: travel back refuses an indoor death and a missing RynthNav", TravelRefusals);
    }

    // ── World ─────────────────────────────────────────────────────────────

    private static (uint Cell, float X, float Y, float Z) Me0 => (0x1048002Au, 100f, 100f, 0f);

    private static void World(string corpseName, string? longDesc, bool closeCalls = false)
    {
        FakeHost.Reset();
        FakeHost.SalvageCalls = true;   // UseObject + MoveItemExternal
        FakeHost.WeaponCalls = true;    // RequestId, HasAppraisalData
        FakeHost.CloseContainerCalls = closeCalls;
        FakeHost.Names[Player] = Me;
        FakeHost.Positions[Player] = Me0;
        FakeHost.PlayerPose = Me0;
        FakeHost.Names[Corpse] = corpseName;
        FakeHost.ItemTypes[Corpse] = ItemTypeContainer;
        if (longDesc != null)
            FakeHost.Strings[(Corpse, LongDescKey)] = longDesc;
        FakeHost.NotAttackable.Add(Corpse);
        FakeHost.Positions[Corpse] = (Me0.Cell, Me0.X + 1.0f, Me0.Y, Me0.Z);
        Item(Gem, "Black Opal", ItemTypeGem);       // the test profile keeps gems
        Item(Cloak, "Faran Cloak", ItemTypeClothing); // ...and nothing else
        Item(Coins, "Pyreal", ItemTypeMoney);
    }

    private static void Item(uint id, string name, uint type)
    {
        FakeHost.Names[id] = name;
        FakeHost.ItemTypes[id] = type;
        FakeHost.Containers[id] = Corpse;   // also installs GetObjectOwnershipInfo
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
            LootOwnership = 0, // My Kills Only
            LootContentSettleMs = 0,
            LootEmptyCorpseMs = 0,
            StopLootingWhenPackFull = false,
        };
        public readonly WorldObjectCache Cache;

        public Bot(RynthCoreHost host)
        {
            typeof(RynthPluginBase).GetMethod("Attach", Any)!.Invoke(P, new object[] { FakeHost.LastApi });
            var dash = (LegacyDashboardRenderer)RuntimeHelpers.GetUninitializedObject(typeof(LegacyDashboardRenderer));
            typeof(LegacyDashboardRenderer).GetField("_settings", Any)!.SetValue(dash, S);
            Set("_dashboard", dash);
            Cache = new WorldObjectCache(host);
            Cache.SetPlayerId(Player);
            Set("_objectCache", Cache);
            Set("_playerId", Player);

            string profile = Path.Combine(Program.TempRoot, "own-corpse-test.json");
            var p = new LootProfile { Name = "gems" };
            var rule = new LootRule { Name = "Gems", Action = LootAction.Keep };
            rule.Conditions.Add(new ObjectClassCondition { ObjectClass = AcObjectClass.Gem });
            p.Rules.Add(rule);
            p.Save(profile);
            S.CurrentLootPath = profile;
        }

        public void Set(string f, object? v) => typeof(RynthAiPlugin).GetField(f, Any)!.SetValue(P, v);
        public T Get<T>(string f) => (T)typeof(RynthAiPlugin).GetField(f, Any)!.GetValue(P)!;
        public object? Call(string m, params object[] a) => typeof(RynthAiPlugin).GetMethod(m, Any)!.Invoke(P, a);
        public bool HasLootWork() => (bool)Call("HasLootWork", S, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())!;
        public void Tick(BotActivity act)
        {
            Set("_activity", act);
            Call("TickCorpseOpening");
        }
        public bool Completed => Get<Dictionary<int, long>>("_completedCorpses").ContainsKey(unchecked((int)Corpse));
        public bool ShouldLoot(uint id) => (bool)Call("ShouldLootCorpse", Cache[unchecked((int)id)]!)!;
    }

    private static Bot NewBot()
    {
        var host = FakeHost.Create(Player);
        var bot = new Bot(host);
        bot.Cache.OnCreateObject(Corpse);
        bot.Cache.Tick();
        return bot;
    }

    /// <summary>Claims and opens the corpse, then ticks; each item the bot moves lands in the pack.</summary>
    private static void LootToTheEnd(Bot bot, int maxTicks = 400)
    {
        bot.Tick(BotActivity.Looting);
        Check.True(FakeHost.Uses.Contains(Corpse), "the corpse is opened");
        foreach (uint id in new[] { Gem, Cloak, Coins })
            bot.Cache.OnCreateObject(id);
        bot.Cache.Tick();
        if (FakeHost.CloseContainerCalls) FakeHost.GroundContainer = Corpse;
        bot.P.OnViewObjectContents(Corpse);

        for (int i = 0; i < maxTicks && !bot.Completed; i++)
        {
            bot.Tick(BotActivity.Looting);
            foreach (var m in FakeHost.ExternalMoves)
                FakeHost.Containers[m.Id] = Player;   // the server moved it
            Thread.Sleep(10);
        }
    }

    private static string Moves() => string.Join(",", FakeHost.ExternalMoves.Select(m => $"{m.Id:X8}->{m.Target:X8}"));

    // ── Identification ────────────────────────────────────────────────────

    private static void Identification()
    {
        OwnCorpseMatch M(string name, string me, string? desc) => RynthAiPlugin.MatchOwnDeathCorpse(name, me, desc);
        Check.Eq(M("Corpse of Dargoth Hera", Me, "Killed by Drudge Skulker."), OwnCorpseMatch.Yes, "your name, killed by a monster");
        Check.Eq(M("Corpse of Dargoth Hera", Me, "Killed by misadventure."), OwnCorpseMatch.Yes, "a fall (misadventure)");
        Check.Eq(M("Corpse of Dargoth Hera", Me, null), OwnCorpseMatch.Pending, "no ID yet: wait for it");
        Check.Eq(M("Corpse of Dargoth Hera", Me, "Killed by Dargoth Hera."), OwnCorpseMatch.No, "a namesake you killed is not your death corpse");
        Check.Eq(M("Corpse of Dargoth Hera", Me, "A corpse."), OwnCorpseMatch.No, "no 'Killed by' line: not a death corpse");
        Check.Eq(M("Corpse of Dargoth Heras", Me, "Killed by Drudge."), OwnCorpseMatch.No, "a longer name");
        Check.Eq(M("Corpse of Dargoth Her", Me, "Killed by Drudge."), OwnCorpseMatch.No, "a shorter name");
        Check.Eq(M("Corpse of dargoth hera", Me, "Killed by Drudge."), OwnCorpseMatch.No, "exact case only");
        Check.Eq(M("Corpse of Dargoth Hera's Wolf", Me, "Killed by Drudge."), OwnCorpseMatch.No, "a pet's corpse");
        Check.Eq(M("Dargoth Hera's corpse", Me, "Killed by Drudge."), OwnCorpseMatch.No, "another naming");
        Check.Eq(M("Corpse of +Dargoth Hera", "+Dargoth Hera", "Killed by Drudge."), OwnCorpseMatch.Yes, "a GM's corpse carries the sigil, as the name does");
        Check.Eq(M("Corpse of Dargoth Hera", "+Dargoth Hera", "Killed by Drudge."), OwnCorpseMatch.No, "a GM never matches the plain name");
        Check.Eq(M("Corpse of Dargoth Hera", "", "Killed by Drudge."), OwnCorpseMatch.No, "no player name yet: nothing matches");
    }

    // ── Recovery ──────────────────────────────────────────────────────────

    private static void RecoveredWhole()
    {
        World("Corpse of " + Me, "Killed by Ancient Water Elemental.", closeCalls: true);
        var bot = NewBot();
        Check.True(bot.ShouldLoot(Corpse), "My Kills Only, killed by a monster: still yours to recover");
        Check.True(bot.HasLootWork(), "looting wants the tick");

        LootToTheEnd(bot);
        Check.True(FakeHost.ExternalMoves.Any(m => m.Id == Gem), "the gem (the profile keeps it) is taken: " + Moves());
        Check.True(FakeHost.ExternalMoves.Any(m => m.Id == Cloak), "the cloak (no profile rule) is taken too: " + Moves());
        Check.True(FakeHost.ExternalMoves.Any(m => m.Id == Coins), "and the pyreals: " + Moves());
        Check.True(FakeHost.ExternalMoves.All(m => m.Target == Player), "into the pack");
        Check.False(FakeHost.Uses.Any(u => u is Gem or Cloak or Coins), "never a use (a use can wield)");
        var first = FakeHost.ExternalMoves.Select(m => m.Id).ToList();
        Check.Eq(first.Distinct().Count(), 3, "one move per item (each confirmed before the next)");
        Check.True(FakeHost.Logs.Any(l => l.Contains("[RynthAi] Own corpse: recovering 3 items.")), "says how many it recovers");
        Check.True(FakeHost.Logs.Any(l => l.Contains("[RynthAi] Own corpse: recovered everything (3 items).")), "and that it got everything");
        Check.True(FakeHost.Chat.Any(l => l.Contains("recovered everything")), "in chat too");
        Check.False(FakeHost.Logs.Any(l => l.Contains("[LootEval]")), "no loot profile evaluation at all");
        Check.True(bot.Completed, "the corpse is completed");
        Check.Eq(string.Join(",", FakeHost.Closes.Select(c => $"{c:X8}")), $"{Corpse:X8}", "and closed, once");
        Check.False(bot.Get<bool>("_currentLootItemIsSalvage"), "nothing queued for salvage");
    }

    private static void SimilarNameUntouched()
    {
        World("Corpse of " + Me + "s", "Killed by Ancient Water Elemental.");
        var bot = NewBot();
        Check.False(bot.ShouldLoot(Corpse), "My Kills Only: a similar name is someone else's corpse");
        Check.False(bot.HasLootWork(), "nothing to loot");
        bot.Tick(BotActivity.Looting);
        Check.Eq(bot.Get<int>("_targetCorpseId"), 0, "not claimed");
        Check.False(FakeHost.Uses.Contains(Corpse), "not opened");

        // Loot From All Corpses: looted by the profile only (the gem), never recovered whole.
        World("Corpse of " + Me + "s", "Killed by Ancient Water Elemental.");
        var all = NewBot();
        all.S.LootOwnership = 2;
        LootToTheEnd(all);
        Check.True(FakeHost.ExternalMoves.Any(m => m.Id == Gem), "All Corpses: the profile's gem: " + Moves());
        Check.False(FakeHost.ExternalMoves.Any(m => m.Id == Cloak || m.Id == Coins), "nothing else: " + Moves());
        Check.False(FakeHost.Logs.Any(l => l.Contains("Own corpse")), "never treated as your corpse");

        // Your exact name but no ID yet: held, not looted by the profile meanwhile.
        World("Corpse of " + Me, null);
        var pending = NewBot();
        pending.S.LootOwnership = 2;
        Check.False(pending.ShouldLoot(Corpse), "named like yours, unidentified: waits for its ID");
        Check.True(FakeHost.IdRequests.Contains(Corpse), "its ID is asked for");
    }

    private static void SettingOffIsOldBehaviour()
    {
        World("Corpse of " + Me, "Killed by Ancient Water Elemental.");
        var bot = NewBot();
        bot.S.LootOwnCorpse = false;
        Check.False(bot.ShouldLoot(Corpse), "My Kills Only: a monster killed you, so it is skipped (as before)");
        Check.False(bot.HasLootWork(), "nothing to loot");

        World("Corpse of " + Me, "Killed by Ancient Water Elemental.");
        var all = NewBot();
        all.S.LootOwnCorpse = false;
        all.S.LootOwnership = 2;
        LootToTheEnd(all);
        Check.True(FakeHost.ExternalMoves.Any(m => m.Id == Gem), "All Corpses: the profile's gem: " + Moves());
        Check.False(FakeHost.ExternalMoves.Any(m => m.Id == Cloak || m.Id == Coins), "only what the profile keeps: " + Moves());
        Check.False(FakeHost.Logs.Any(l => l.Contains("Own corpse")), "no own-corpse recovery");
    }

    private static void PriorityAndTimeout()
    {
        const uint Kill = 0x80261200;
        World("Corpse of " + Me, "Killed by Ancient Water Elemental.");
        FakeHost.Positions[Corpse] = (Me0.Cell, Me0.X + 4.0f, Me0.Y, Me0.Z);   // yours, 4 m
        FakeHost.Names[Kill] = "Corpse of Drudge Skulker";
        FakeHost.ItemTypes[Kill] = ItemTypeContainer;
        FakeHost.Strings[(Kill, LongDescKey)] = $"Killed by {Me}.";
        FakeHost.NotAttackable.Add(Kill);
        FakeHost.Positions[Kill] = (Me0.Cell, Me0.X + 1.0f, Me0.Y, Me0.Z);    // your kill, 1 m
        var bot = NewBot();
        bot.Cache.OnCreateObject(Kill);
        bot.Cache.Tick();
        Check.True(bot.ShouldLoot(Kill), "your kill is lootable");
        bot.Tick(BotActivity.Looting);
        Check.Eq(bot.Get<int>("_targetCorpseId"), unchecked((int)Corpse), "your own corpse is claimed first, though further");

        // It timed out once it was opened: not completed (that would leave your things for good).
        bot.P.OnViewObjectContents(Corpse);
        bot.Call("AbandonCurrentCorpse", Environment.TickCount64, "timeout");
        Check.False(bot.Completed, "an own corpse that timed out is not written off");
        Check.True(bot.Get<Dictionary<int, long>>("_corpseCooldownUntil").ContainsKey(unchecked((int)Corpse)), "it is retried after a short wait");
        for (int i = 1; i < RynthAiPlugin.OwnCorpseMaxAbandons; i++)
        {
            bot.P.OnViewObjectContents(Corpse);
            bot.Call("AbandonCurrentCorpse", Environment.TickCount64, "timeout");
        }
        Check.True(bot.Completed, $"after {RynthAiPlugin.OwnCorpseMaxAbandons} timeouts it is given up");
        Check.True(FakeHost.Chat.Any(l => l.Contains("Own corpse: giving up")), "and it says so");
    }

    // ── Settings ──────────────────────────────────────────────────────────

    private static void SettingsDefaults()
    {
        Check.True(new LegacyUiSettings().LootOwnCorpse, "Recover My Corpse: on for a new profile");
        Check.False(new LegacyUiSettings().TravelToOwnCorpse, "Travel Back To My Corpse: off for a new profile");
        var old = JsonSerializer.Deserialize("{\"EnableLooting\":true,\"LootOwnership\":0}", RynthAiJsonContext.Default.LegacyUiSettings);
        Check.True(old != null && old.LootOwnCorpse, "a profile saved before the setting: on");
        Check.True(old != null && !old.TravelToOwnCorpse, "and travel off");
        var saved = JsonSerializer.Deserialize(
            JsonSerializer.Serialize(new LegacyUiSettings { LootOwnCorpse = false, TravelToOwnCorpse = true }, RynthAiJsonContext.Default.LegacyUiSettings),
            RynthAiJsonContext.Default.LegacyUiSettings);
        Check.True(saved != null && !saved.LootOwnCorpse && saved.TravelToOwnCorpse, "both are saved and loaded");

        var copy = typeof(LegacyDashboardRenderer).GetMethod("CopySettings", BindingFlags.NonPublic | BindingFlags.Static)!;
        var dst = new LegacyUiSettings();
        copy.Invoke(null, new object[] { new LegacyUiSettings { LootOwnCorpse = false, TravelToOwnCorpse = true }, dst });
        Check.True(!dst.LootOwnCorpse && dst.TravelToOwnCorpse, "a profile switch copies both");
        copy.Invoke(null, new object[] { old!, dst });
        Check.True(dst.LootOwnCorpse && !dst.TravelToOwnCorpse, "an old profile loads the defaults");

        // The engine's Settings face: sent, and an engine without the fields leaves them alone.
        var payload = JsonSerializer.Deserialize("{\"PatrolOnLogin\":false}", RynthAiJsonContext.Default.SettingsBridgePayload);
        Check.True(payload != null && payload.LootOwnCorpse == null && payload.TravelToOwnCorpse == null, "an older engine's payload has no value");
        var s = new LegacyUiSettings { LootOwnCorpse = false, TravelToOwnCorpse = true };
        var dash = (LegacyDashboardRenderer)RuntimeHelpers.GetUninitializedObject(typeof(LegacyDashboardRenderer));
        typeof(LegacyDashboardRenderer).GetField("_settings", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(dash, s);
        typeof(LegacyDashboardRenderer).GetField("_advancedSettingsUi", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(dash, new LegacyAdvancedSettingsUi(s));
        var sent = JsonSerializer.Deserialize(dash.BuildSettingsJson(), RynthAiJsonContext.Default.SettingsBridgePayload);
        Check.True(sent != null && sent.LootOwnCorpse == false && sent.TravelToOwnCorpse == true, "sent to the engine face");
        dash.ApplySettingsJson("{\"PatrolOnLogin\":false}");
        Check.True(!s.LootOwnCorpse && s.TravelToOwnCorpse, "a save without the fields leaves them");
        dash.ApplySettingsJson("{\"LootOwnCorpse\":true,\"TravelToOwnCorpse\":false}");
        Check.True(s.LootOwnCorpse && !s.TravelToOwnCorpse, "a save with them sets them");
    }

    // ── Death spot ────────────────────────────────────────────────────────

    private static Bot DieAt(double ns, double ew, uint? cell = null, bool broker = false, bool navLoaded = true)
    {
        World("Corpse of " + Me, "Killed by Ancient Water Elemental.");
        FakeHost.PluginCommandCalls = broker;
        FakeHost.PluginCommandDelivered = navLoaded;
        FakeHost.SetNavPosition(ns, ew);
        if (cell is uint c) FakeHost.PlayerPose = (c, 50f, 60f, -12f);
        FakeHost.Positions[Player] = FakeHost.PlayerPose;
        var bot = NewBot();
        var vitals = bot.Get<PlayerVitalsCache>("_vitals");
        vitals.MaxHealth = 100;
        vitals.CurrentHealth = 0;
        bot.Call("CheckPlayerDeath", bot.S);
        return bot;
    }

    private static void DeathSpotRemembered()
    {
        var bot = DieAt(42.10, 33.60);
        var spot = bot.P.DeathSpot;
        Check.True(spot != null, "the death spot is remembered");
        Check.True(spot != null && Math.Abs(spot.Ns - 42.10) < 0.01 && Math.Abs(spot.Ew - 33.60) < 0.01, $"at the death position ({spot?.Ns:0.00}, {spot?.Ew:0.00})");
        Check.True(spot != null && spot.Outdoors && spot.Cell == FakeHost.PlayerPose.Cell, "with its landblock and cell");
        Check.True(FakeHost.Logs.Any(l => l.Contains("[RynthAi] Your corpse is at 42.10N, 33.60E (landblock 0x")), "logged: " + string.Join(" | ", FakeHost.Logs.Where(l => l.Contains("corpse is at"))));
        Check.True(FakeHost.Chat.Any(l => l.Contains("Your corpse is at 42.10N, 33.60E")), "and said in chat");

        bot.Call("OnOwnCorpseChat", "You have retained all your items. You do not need to recover your corpse!");   // OnChatWindowText hands chat lines here
        Check.True(bot.P.DeathSpot!.NothingDropped, "the server's 'nothing dropped' line is noted");

        var indoor = DieAt(0, 0, cell: 0x01D90123u);
        Check.True(indoor.P.DeathSpot != null && !indoor.P.DeathSpot.Outdoors, "a dungeon death is remembered as indoors");
        Check.True(FakeHost.Logs.Any(l => l.Contains("Your corpse is at landblock 0x01D9, cell 0x0123")), "with landblock and cell");

        // No position at the death: the server's line gives the spot.
        World("Corpse of " + Me, "Killed by Ancient Water Elemental.");
        var noPose = NewBot();
        noPose.Call("OnOwnCorpseChat", "Your corpse is located at (12.3S, 45.6W).");   // OnChatWindowText hands chat lines here
        var fromChat = noPose.P.DeathSpot;
        Check.True(fromChat != null && Math.Abs(fromChat.Ns + 12.3) < 0.001 && Math.Abs(fromChat.Ew + 45.6) < 0.001, "taken from the server's line");
    }

    // ── Travel back ───────────────────────────────────────────────────────

    private static void BackdateIdle(Bot bot) => bot.Set("_travelIdleSince", Environment.TickCount64 - 10_000);

    private static void TravelBack()
    {
        var bot = DieAt(42.10, 33.60, broker: true);
        bot.S.TravelToOwnCorpse = true;

        // Respawned at a lifestone ~100 m away.
        FakeHost.SetNavPosition(42.50, 33.60);
        FakeHost.Positions[Player] = FakeHost.PlayerPose;
        bot.Call("OnRespawnedForOwnCorpse");
        Check.True(bot.Get<bool>("_ownCorpseTravelManual") == false, "started by the respawn, not by hand");
        Check.True((bool)typeof(RynthAiPlugin).GetProperty("OwnCorpseTravelHoldsNav", Any)!.GetValue(bot.P)!, "RynthAi's own route is parked meanwhile");

        bot.Set("_activity", BotActivity.Idle);
        bot.Call("TickOwnCorpseTravel", bot.S);
        Check.Eq(FakeHost.PluginCommands.Count, 0, "waits a moment of quiet before walking");
        BackdateIdle(bot);
        bot.Call("TickOwnCorpseTravel", bot.S);
        Check.True(FakeHost.PluginCommands.Contains(("RynthNav", "rnav", "go 42.10N, 33.60E")),
            "walks with RynthNav: " + string.Join(" | ", FakeHost.PluginCommands));

        // A monster: RynthAi fights, the walk stops.
        bot.Set("_activity", BotActivity.Combat);
        bot.Call("TickOwnCorpseTravel", bot.S);
        Check.Eq(FakeHost.PluginCommands.Last(), ("RynthNav", "rnav", "stop"), "the walk stops for combat");
        int count = FakeHost.PluginCommands.Count;
        bot.Call("TickOwnCorpseTravel", bot.S);
        Check.Eq(FakeHost.PluginCommands.Count, count, "one stop, not one per tick");
        Check.True(FakeHost.Logs.Any(l => l.Contains("travel paused for Combat")), "logged");

        // Fight over: walk on.
        bot.Set("_activity", BotActivity.Idle);
        bot.Call("TickOwnCorpseTravel", bot.S);
        BackdateIdle(bot);
        bot.Call("TickOwnCorpseTravel", bot.S);
        Check.Eq(FakeHost.PluginCommands.Last(), ("RynthNav", "rnav", "go 42.10N, 33.60E"), "resumes after the fight");

        // Near the spot, the corpse is in loot range: travel ends, looting takes over.
        FakeHost.SetNavPosition(42.10, 33.61);   // ~2.4 m east... 0.01 coord = 2.4 m
        FakeHost.Positions[Player] = FakeHost.PlayerPose;
        var p = FakeHost.PlayerPose;
        FakeHost.Positions[Corpse] = (p.Cell, p.X + 1.0f, p.Y, p.Z);
        bot.Cache.OnCreateObject(Corpse);
        bot.Cache.Tick();
        bot.Call("TickOwnCorpseTravel", bot.S);
        Check.Eq(FakeHost.PluginCommands.Last(), ("RynthNav", "rnav", "stop"), "the walk stops");
        Check.False((bool)typeof(RynthAiPlugin).GetProperty("OwnCorpseTravelHoldsNav", Any)!.GetValue(bot.P)!, "travel is over");
        Check.True(FakeHost.Logs.Any(l => l.Contains("travel back ended: your corpse") && l.Contains("is in loot range")), "says why");
        Check.True(bot.HasLootWork(), "and looting wants the tick for it");

        // Timeout with no progress.
        var stuck = DieAt(42.10, 33.60, broker: true);
        stuck.S.TravelToOwnCorpse = true;
        FakeHost.SetNavPosition(42.50, 33.60);
        FakeHost.Positions[Player] = FakeHost.PlayerPose;
        stuck.Call("OnRespawnedForOwnCorpse");
        stuck.Set("_activity", BotActivity.Idle);
        BackdateIdle(stuck);
        stuck.Call("TickOwnCorpseTravel", stuck.S);
        stuck.Call("TickOwnCorpseTravel", stuck.S);   // first distance sample
        stuck.Set("_travelBestAt", Environment.TickCount64 - RynthAiPlugin.OwnCorpseTravelNoProgressMs - 1000);
        stuck.Call("TickOwnCorpseTravel", stuck.S);
        Check.True(FakeHost.Logs.Any(l => l.Contains("travel back ended: gave up: no closer")), "no progress: gives up and says so");
        Check.Eq(FakeHost.PluginCommands.Last(), ("RynthNav", "rnav", "stop"), "and stops RynthNav");

        // Nothing dropped: no trip.
        var none = DieAt(42.10, 33.60);
        none.Call("OnOwnCorpseChat", "You have retained all your items. You do not need to recover your corpse!");   // OnChatWindowText hands chat lines here
        none.S.TravelToOwnCorpse = true;
        none.Call("OnRespawnedForOwnCorpse");
        Check.False((bool)typeof(RynthAiPlugin).GetProperty("OwnCorpseTravelHoldsNav", Any)!.GetValue(none.P)!, "nothing dropped: no trip back");

        // Setting off (the default): no trip.
        var off = DieAt(42.10, 33.60);
        off.Call("OnRespawnedForOwnCorpse");
        Check.False((bool)typeof(RynthAiPlugin).GetProperty("OwnCorpseTravelHoldsNav", Any)!.GetValue(off.P)!, "Travel Back off: no trip back");
    }

    private static void TravelRefusals()
    {
        var indoor = DieAt(0, 0, cell: 0x01D90123u, broker: true);
        indoor.S.TravelToOwnCorpse = true;
        indoor.Call("OnRespawnedForOwnCorpse");
        Check.False((bool)typeof(RynthAiPlugin).GetProperty("OwnCorpseTravelHoldsNav", Any)!.GetValue(indoor.P)!, "an indoor death: no trip");
        Check.True(FakeHost.Chat.Any(l => l.Contains("you died indoors") && l.Contains("only works outdoors")), "and it says why");

        var noNav = DieAt(42.10, 33.60, broker: true, navLoaded: false);   // RynthNav not loaded
        noNav.S.TravelToOwnCorpse = true;
        FakeHost.SetNavPosition(42.50, 33.60);
        FakeHost.Positions[Player] = FakeHost.PlayerPose;
        noNav.Call("OnRespawnedForOwnCorpse");
        noNav.Set("_activity", BotActivity.Idle);
        BackdateIdle(noNav);
        noNav.Call("TickOwnCorpseTravel", noNav.S);
        Check.False((bool)typeof(RynthAiPlugin).GetProperty("OwnCorpseTravelHoldsNav", Any)!.GetValue(noNav.P)!, "RynthNav missing: the trip ends");
        Check.True(FakeHost.Chat.Any(l => l.Contains("RynthNav isn't loaded")), "and it says why");
    }
}

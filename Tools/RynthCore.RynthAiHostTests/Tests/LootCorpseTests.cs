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
using RynthCore.Plugin.Shared;
using RynthCore.PluginCore;
using RynthCore.PluginSdk;
using RynthCore.RynthAiHostTests.Fakes;
using RynthCore.RynthAiTests.Harness;

namespace RynthCore.RynthAiHostTests.Tests;

// 2026-10-03, Dargoth Hera on a busy server: RynthAi never looted his own kills, in "My Kills
// Only" and in "Loot All". Two causes:
//  1. A fresh corpse whose name and type (Container) were in the engine's identity snapshot
//     before its position was got filed as an inventory Container ("nopos-item") and was never
//     looked at again, so it was never a loot candidate. Your own kills hit it most: they are
//     created right next to you and classified at once.
//  2. Combat outranks looting, and a busy spawn always has a monster inside MonsterRange, so
//     a corpse waited until the area emptied. A lootable corpse in range that has waited
//     LootStarveMs now gets a turn while no monster is within LootStarveCloseYards.
internal static class LootCorpseTests
{
    private const uint Player = 0x50000D01;
    private const uint Corpse = 0x80261100;
    private const uint Gem = 0x80261101;
    private const uint Monster = 0x80246096;
    private const uint ItemTypeContainer = 0x200, ItemTypeGem = 0x800, ItemTypeCreature = 0x10;
    private const uint BfCorpse = 0x2000;
    private const uint LongDescKey = 16;
    private const string Me = "Dargoth Hera";
    private const BindingFlags Any = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

    public static void Register(Runner r)
    {
        r.Add("corpse: created before its position is readable, it is still a Corpse on the landscape", CorpseWithoutPositionIsCorpse);
        r.Add("corpse: the weenie corpse flag makes a nameless corpse a Corpse", CorpseFlagWithoutName);
        r.Add("corpse: one stuck in the inventory as a Container is moved to the landscape", CorpseInInventoryRescued);
        r.Add("corpse: a corpse named late (stored nameless) becomes a Corpse on access", CorpseNamedLateOnAccess);
        r.Add("loot chain: own corpse appears -> claimed -> opened -> item moved into the pack", FullLootChain);
        r.Add("loot chain: 'My Kills Only' loots your kill and skips another player's", OwnershipModes);
        r.Add("loot chain: an item the cache never saw created is found in the corpse's contents and looted", UnseenItemRescued);
        r.Add("loot chain: without GetContainerContents an unseen item stays unseen (no crash)", UnseenItemOldEngine);
        r.Add("loot starvation: a corpse waiting behind far-off combat gets a turn, a close monster keeps combat", LootStarvation);
        r.Add("loot logging: a corpse held by combat is logged once, with the reason", HeldByCombatLoggedOnce);
        r.Add("boost loot: our kill holds the next target until its corpse is looted (one kill, one loot)", BoostLootOneKillOneLoot);
        r.Add("boost loot: the wait ends after BoostLootKillWaitMs, for a close monster, and with Boost Loot off", BoostLootHoldLimits);
        r.Add("boost loot: looting the kill's corpse ends the wait; an older corpse does not", BoostLootWaitEndsOnKillCorpse);
        r.Add("monster ignore: a close Priority 0 monster neither holds combat nor starves the corpse", IgnoredMonsterDoesNotHoldLoot);
        r.Add("loot gate (R-20261025 727b): a predicted kill still alive holds the corpse; after its window combat has it again", PredictedKillHoldsCorpse);
        r.Add("loot gate (R-20261025 727b): the kill notice, health 0 or a hit after the swap settle a predicted kill at once", PredictedKillSettled);
        r.Add("corpse close: a finished corpse is closed once (CloseContainer, API v76)", FinishedCorpseClosedOnce);
        r.Add("corpse close: an engine without CloseContainer leaves it to the next open (no close)", FinishedCorpseNotClosedOnOldEngine);
        r.Add("corpse close: an abandoned open corpse is closed once", AbandonedCorpseClosed);
        r.Add("corpse close: stopping the macro mid-loot closes the open corpse, not marked completed", MacroStopClosesCorpse);
        r.Add("loot pacing: a pickup waits for the last bend-down, and no retry is timed while it plays", PickupWaitsForAnimation);
        r.Add("loot: a pack that takes none of an item's tries is left out; the item tries another, else looting stops", RefusingPackIsLeftOut);
        r.Add("loot pacing: no pickup (first or retry) while the server owes an answer to a cast; it goes on the UseDone", PickupWaitsForCastAnswer);
        r.Add("corpse close: not while the last item is between the corpse and the pack - it closes once the item is in the inventory", CloseWaitsForInventory);
        r.Add("loot pacing: an item merged into a stack in the pack is confirmed without the 4 s arrival wait", MergedPickupConfirmedQuickly);
        r.Add("loot pacing: each item taken restarts the corpse timeout, so a corpse with many keeps is looted to the end", ManyKeepsBeatTheTimeout);
    }

    // ── World set-up ──────────────────────────────────────────────────────────

    private static (uint Cell, float X, float Y, float Z) Me0 => (0x1048002Au, 100f, 100f, 0f);

    private static void BaseWorld(bool corpsePositioned)
    {
        FakeHost.Reset();
        FakeHost.SalvageCalls = true; // UseObject + MoveItemExternal
        FakeHost.WeaponCalls = true;  // RequestId, HasAppraisalData
        FakeHost.Names[Player] = Me;
        FakeHost.Positions[Player] = Me0;
        FakeHost.PlayerPose = Me0;
        FakeHost.Names[Corpse] = "Corpse of Ancient Water Elemental";
        FakeHost.ItemTypes[Corpse] = ItemTypeContainer;
        FakeHost.Strings[(Corpse, LongDescKey)] = $"Killed by {Me}.";
        FakeHost.NotAttackable.Add(Corpse);
        if (corpsePositioned)
            FakeHost.Positions[Corpse] = (Me0.Cell, Me0.X + 1.0f, Me0.Y, Me0.Z);
        FakeHost.Names[Gem] = "Black Opal";
        FakeHost.ItemTypes[Gem] = ItemTypeGem;
        FakeHost.Containers[Gem] = Corpse;   // also installs GetObjectOwnershipInfo
        FakeHost.Appraised.Add(Gem);
    }

    // ── Classification (cause 1) ──────────────────────────────────────────────

    private static void CorpseWithoutPositionIsCorpse()
    {
        BaseWorld(corpsePositioned: false);
        var host = FakeHost.Create(Player);
        var cache = new WorldObjectCache(host);
        cache.SetPlayerId(Player);

        cache.OnCreateObject(Corpse);
        cache.Tick();

        Check.Eq(cache[unchecked((int)Corpse)]?.ObjectClass, AcObjectClass.Corpse, "a Corpse, not an inventory Container");
        Check.True(cache.GetLandscapeObjects().Any(o => o.Id == unchecked((int)Corpse)), "on the landscape (loot candidates come from there)");
        Check.False(cache.GetInventory().Any(o => o.Id == unchecked((int)Corpse)), "not in the inventory");

        FakeHost.Positions[Corpse] = (Me0.Cell, Me0.X + 1.0f, Me0.Y, Me0.Z);
        double d = cache.Distance(unchecked((int)Player), unchecked((int)Corpse));
        Check.True(d < 1.5, $"its distance reads once the position lands (d={d:0.00})");
    }

    private static void CorpseFlagWithoutName()
    {
        BaseWorld(corpsePositioned: true);
        FakeHost.Names.Remove(Corpse);
        FakeHost.Bitfields[Corpse] = BfCorpse;
        var host = FakeHost.Create(Player);
        var cache = new WorldObjectCache(host);
        cache.SetPlayerId(Player);

        cache.OnCreateObject(Corpse);
        cache.Tick();

        Check.Eq(cache[unchecked((int)Corpse)]?.ObjectClass, AcObjectClass.Corpse, "the corpse flag alone makes it a Corpse");
        FakeHost.Names[Corpse] = "Corpse of Ancient Water Elemental";
        Check.Eq(cache[unchecked((int)Corpse)]?.Name, "Corpse of Ancient Water Elemental", "the name fills in later");
    }

    private static void CorpseInInventoryRescued()
    {
        // The old trap, reproduced directly: an entry filed as an inventory Container (as
        // TryClassify did before the fix when the position wasn't readable yet).
        BaseWorld(corpsePositioned: false);
        FakeHost.Names.Remove(Corpse);
        var host = FakeHost.Create(Player);
        var cache = new WorldObjectCache(host);
        cache.SetPlayerId(Player);
        _ = cache[unchecked((int)Corpse)]; // nameless, positionless: the lazy lookup says "not yet"
        // File it as the old code did.
        var byId = (Dictionary<int, WorldObject>)typeof(WorldObjectCache).GetField("_byId", Any)!.GetValue(cache)!;
        var inv = (HashSet<int>)typeof(WorldObjectCache).GetField("_inventory", Any)!.GetValue(cache)!;
        var make = typeof(WorldObjectCache).GetMethod("Make", Any)!;
        lock (typeof(WorldObjectCache).GetField("_gate", Any)!.GetValue(cache)!)
        {
            byId[unchecked((int)Corpse)] = (WorldObject)make.Invoke(cache, new object[] { unchecked((int)Corpse), "", AcObjectClass.Container })!;
            inv.Add(unchecked((int)Corpse));
        }
        FakeHost.Names[Corpse] = "Corpse of Ancient Water Elemental";
        FakeHost.Positions[Corpse] = (Me0.Cell, Me0.X + 1.0f, Me0.Y, Me0.Z);

        typeof(WorldObjectCache).GetField("_lastReclassifyTime", Any)!.SetValue(cache, DateTime.MinValue);
        cache.Tick(); // the 2 s pass

        var wo = byId[unchecked((int)Corpse)];
        Check.Eq(wo.ObjectClass, AcObjectClass.Corpse, "rescued to Corpse");
        Check.True(cache.GetLandscapeObjects().Any(o => o.Id == unchecked((int)Corpse)), "and on the landscape");
        Check.True(FakeHost.Logs.Any(l => l.Contains("was filed as Container")), "the rescue is logged");
    }

    private static void CorpseNamedLateOnAccess()
    {
        BaseWorld(corpsePositioned: true);
        FakeHost.Names.Remove(Corpse);
        FakeHost.ItemTypes.Remove(Corpse);
        var host = FakeHost.Create(Player);
        var cache = new WorldObjectCache(host);
        cache.SetPlayerId(Player);
        _ = cache[unchecked((int)Corpse)];  // positioned, nameless, typeless: Unknown landscape
        Check.Eq(cache[unchecked((int)Corpse)]?.ObjectClass, AcObjectClass.Unknown, "stored as Unknown first");

        FakeHost.Names[Corpse] = "Corpse of Ancient Water Elemental";
        Check.Eq(cache[unchecked((int)Corpse)]?.ObjectClass, AcObjectClass.Corpse, "a Corpse as soon as its name reads");
    }

    // ── The plugin, without Initialize (no UI, no disk) ───────────────────────

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

            string profile = Path.Combine(Program.TempRoot, "loot-corpse-test.json");
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
    }

    private static void FullLootChain()
    {
        BaseWorld(corpsePositioned: false);   // identity before position: the old trap
        var host = FakeHost.Create(Player);
        var bot = new Bot(host);

        bot.Cache.OnCreateObject(Corpse);
        bot.Cache.Tick();
        Check.Eq(bot.Cache[unchecked((int)Corpse)]?.ObjectClass, AcObjectClass.Corpse, "the fresh corpse is a Corpse");

        FakeHost.Positions[Corpse] = (Me0.Cell, Me0.X + 1.0f, Me0.Y, Me0.Z);
        Check.True(bot.HasLootWork(), "looting wants the tick: an own corpse in range");

        bot.Tick(BotActivity.Looting);
        Check.Eq(bot.Get<int>("_targetCorpseId"), unchecked((int)Corpse), "the corpse is claimed");
        Check.True(FakeHost.Logs.Any(l => l.Contains("claimed corpse target")), "claim logged");
        Check.True(FakeHost.Uses.Contains(Corpse), "the corpse is opened (UseObject on it)");

        // The server opens it: the item arrives, then ViewContents.
        bot.Cache.OnCreateObject(Gem);
        bot.Cache.Tick();
        bot.P.OnViewObjectContents(Corpse);

        for (int i = 0; i < 100 && !FakeHost.ExternalMoves.Any(m => m.Id == Gem); i++)
        {
            bot.Tick(BotActivity.Looting);
            Thread.Sleep(10);
        }
        Check.True(FakeHost.ExternalMoves.Contains((Gem, Player, 0)),
            "the gem is moved into the main pack (moves: " + string.Join(",", FakeHost.ExternalMoves.Select(m => $"{m.Id:X8}->{m.Target:X8}")) + ")");
        Check.False(FakeHost.Uses.Contains(Gem), "never used (a use can wield)");
    }

    // 2026-10-11, Silentkelpie: a Necrolytic Extract on his own kill, a Keep rule for it, left on
    // the corpse with no [LootEval] line. The cache never processed a create for it, so it was
    // in neither the inventory nor the landscape set and GetContainedItems never returned it.
    private static void UnseenItemRescued()
    {
        BaseWorld(corpsePositioned: true);
        FakeHost.ContentsCalls = true;
        var host = FakeHost.Create(Player);
        var bot = new Bot(host);
        bot.Cache.OnCreateObject(Corpse);
        bot.Cache.Tick();
        Check.True(bot.HasLootWork(), "looting wants the tick");
        bot.Tick(BotActivity.Looting);
        Check.Eq(bot.Get<int>("_targetCorpseId"), unchecked((int)Corpse), "the corpse is claimed");

        // The gem is in the corpse but its create never reaches the cache.
        bot.P.OnViewObjectContents(Corpse);
        Check.False(bot.Cache.GetContainedItems(unchecked((int)Corpse)).Any(), "the cache has nothing for the corpse (the bug's state)");

        for (int i = 0; i < 100 && !FakeHost.ExternalMoves.Any(m => m.Id == Gem); i++)
        {
            bot.Tick(BotActivity.Looting);
            Thread.Sleep(10);
        }
        Check.True(FakeHost.Logs.Any(l => l.Contains("the object cache had missed") && l.Contains("Black Opal")), "the rescue is logged with the item");
        Check.True(FakeHost.ExternalMoves.Contains((Gem, Player, 0)), "the unseen gem is looted");
    }

    private static void UnseenItemOldEngine()
    {
        BaseWorld(corpsePositioned: true);
        var host = FakeHost.Create(Player);   // no GetContainerContents
        var bot = new Bot(host);
        bot.Cache.OnCreateObject(Corpse);
        bot.Cache.Tick();
        bot.Tick(BotActivity.Looting);
        bot.P.OnViewObjectContents(Corpse);
        for (int i = 0; i < 20; i++) { bot.Tick(BotActivity.Looting); Thread.Sleep(5); }
        Check.False(FakeHost.ExternalMoves.Any(m => m.Id == Gem), "nothing to find it with: not looted");
        Check.False(FakeHost.Logs.Any(l => l.Contains("the object cache had missed")), "no rescue line");
    }

    private static void OwnershipModes()
    {
        BaseWorld(corpsePositioned: true);
        const uint Other = 0x80261200;
        FakeHost.Names[Other] = "Corpse of Ancient Water Elemental";
        FakeHost.ItemTypes[Other] = ItemTypeContainer;
        FakeHost.Strings[(Other, LongDescKey)] = "Killed by Charlie.";
        FakeHost.Positions[Other] = (Me0.Cell, Me0.X, Me0.Y + 1.0f, Me0.Z);
        var host = FakeHost.Create(Player);
        var bot = new Bot(host);
        bot.Cache.OnCreateObject(Corpse);
        bot.Cache.OnCreateObject(Other);
        bot.Cache.Tick();

        bool Loot(uint id) => (bool)bot.Call("ShouldLootCorpse", bot.Cache[unchecked((int)id)]!)!;
        Check.True(Loot(Corpse), "My Kills Only: your own kill is looted");
        Check.False(Loot(Other), "My Kills Only: another player's kill is skipped");
        bot.S.LootOwnership = 2;
        Check.True(Loot(Other), "All Corpses: anyone's kill is looted");
    }

    private static Bot StarvationWorld(float monsterMeters, out CombatManager combat, Action<LegacyUiSettings>? configure = null)
    {
        BaseWorld(corpsePositioned: true);
        FakeHost.Names[Monster] = "Ancient Water Elemental";
        FakeHost.ItemTypes[Monster] = ItemTypeCreature;
        FakeHost.Positions[Monster] = (Me0.Cell, Me0.X + monsterMeters, Me0.Y + 0.5f, Me0.Z);
        var host = FakeHost.Create(Player);
        var bot = new Bot(host);
        bot.S.EnableCombat = true;
        bot.S.MonsterRange = 50;
        configure?.Invoke(bot.S);
        bot.Cache.OnCreateObject(Corpse);
        _ = bot.Cache[unchecked((int)Monster)];
        bot.Cache.OnUpdateHealth(Monster, 1f);
        bot.Cache.Tick();
        combat = new CombatManager(host, bot.S, bot.Cache);
        combat.SetPlayerId(Player);
        typeof(CombatManager).GetField("_lastScanTime", Any)?.SetValue(combat, DateTime.MinValue);
        combat.ScanNearbyTargets();
        bot.Set("_combatManager", combat);
        return bot;
    }

    private static void AgeCorpse(Bot bot, long ms)
    {
        var waiting = bot.Get<Dictionary<int, long>>("_corpseWaitingSince");
        waiting[unchecked((int)Corpse)] = Environment.TickCount64 - ms;
    }

    private static void LootStarvation()
    {
        var bot = StarvationWorld(15f, out var combat);
        Check.True(combat.HasTargets, "combat has a target in range (15 m)");
        Check.True(combat.HasEngageableTarget, "and wants the tick");

        Check.True(bot.HasLootWork(), "the corpse is lootable and in range");
        Check.False(bot.Get<bool>("_lootStarved"), "fresh: not starved yet");
        var noStarve = new ArbiterInputs(true, false, true, true, false, true, combatEngaged: true);
        Check.Eq(ActivityArbiter.Decide(in noStarve), BotActivity.Combat, "combat first while the corpse is fresh");

        bot.Tick(BotActivity.Combat);
        Check.Eq(bot.Get<int>("_targetCorpseId"), 0, "no claim while combat holds the tick");

        AgeCorpse(bot, RynthAiPlugin.LootStarveMs + 1000);
        Check.True(bot.HasLootWork(), "still lootable");
        Check.True(bot.Get<bool>("_lootStarved"), "waited past LootStarveMs: starved");
        Check.True((bool)bot.Call("IsLootStarvedTurn", true, true)!, "no monster within 5 yd: looting gets a turn");
        var starve = new ArbiterInputs(true, false, true, true, false, true, combatEngaged: true, lootStarved: true);
        Check.Eq(ActivityArbiter.Decide(in starve), BotActivity.Looting, "the arbiter gives Looting the tick");

        bot.Set("_lootStarveTurn", true);
        bot.Tick(BotActivity.Looting);
        Check.Eq(bot.Get<int>("_targetCorpseId"), unchecked((int)Corpse), "the starved corpse is claimed despite combat targets");
        Check.True(FakeHost.Uses.Contains(Corpse), "and opened");

        // A monster on top of you keeps combat in charge.
        var close = StarvationWorld(2f, out var combat2);
        Check.True(combat2.HasCloseThreat(RynthAiPlugin.LootStarveCloseYards), "a monster within 5 yd");
        AgeCorpse(close, RynthAiPlugin.LootStarveMs + 1000);
        Check.True(close.HasLootWork(), "corpse lootable");
        Check.False((bool)close.Call("IsLootStarvedTurn", true, true)!, "close monster: no loot turn, combat keeps the tick");
        var buffing = new ArbiterInputs(true, true, true, true, false, true, lootStarved: true);
        Check.Eq(ActivityArbiter.Decide(in buffing), BotActivity.Buffing, "buffing still outranks a starved corpse");
    }

    private static void IgnoredMonsterDoesNotHoldLoot()
    {
        // The same close (2 m) Water Elemental that keeps combat in charge above, with its rule at Priority 0.
        var bot = StarvationWorld(2f, out var combat, s => s.MonsterRules = new List<MonsterRule>
        {
            new MonsterRule { Name = "Default", Priority = 1 },
            new MonsterRule { Name = "Water Elemental", Priority = 0 },
        });
        Check.False(combat.HasTargets, "an ignored monster is not a target");
        Check.False(combat.HasNearbyMonsters, "and not a monster nearby");
        Check.False(combat.HasCloseThreat(RynthAiPlugin.LootStarveCloseYards), "nor a close threat that keeps the starved corpse waiting");
        Check.False(combat.HasEngageableTarget, "combat does not want the tick");
        AgeCorpse(bot, RynthAiPlugin.LootStarveMs + 1000);
        Check.True(bot.HasLootWork(), "the corpse is lootable");
        var inputs = new ArbiterInputs(true, false, combat.HasEngageableTarget, true, false, true, combatEngaged: combat.HasEngageableTarget);
        Check.Eq(ActivityArbiter.Decide(in inputs), BotActivity.Looting, "so looting has the tick without needing a starved turn");
    }

    // ── A predicted kill that is still alive (report R-20261025-183343-727b, Conquest) ─────────
    // The bot swapped off a Curator of Torment as a predicted kill (34% health left), the Rynthid it
    // locked next died at once, and with the Curator kept out of the scan HasTargets went false:
    // "Corpse loot: claimed corpse target", 4.6 s of looting with the live Curator 1.9 m away.

    /// <summary>The Water Elemental 2 m away fought, then swapped off as a predicted kill (dropped,
    /// out of the scan), with combat's target clock at <paramref name="now"/>.</summary>
    private static Bot PredictedKillWorld(out CombatManager combat, Func<DateTime> now)
    {
        var bot = StarvationWorld(2f, out combat);
        FakeHost.Wcids[Monster] = 40030;
        combat.TargetClock = now;
        combat.SetDamageStores(null, new RynthCore.Plugin.RynthAi.CreatureData.MonsterDamageStore());
        combat.EngageForTest(unchecked((int)Monster));
        combat.CastAndPredictForTest(0x8008AB76, "Fire", 8);   // one cast at it (the kill notice credits it)
        combat.PredictedSwapForTest();
        Rescan(combat);
        return bot;
    }

    private static void Rescan(CombatManager combat)
    {
        typeof(CombatManager).GetField("_lastScanTime", Any)?.SetValue(combat, DateTime.MinValue);
        combat.ScanNearbyTargets();
    }

    private static void PredictedKillHoldsCorpse()
    {
        DateTime now = new(2026, 10, 25, 19, 32, 58);
        var bot = PredictedKillWorld(out var combat, () => now);
        int mob = unchecked((int)Monster);
        Check.Eq(combat.activeTargetId, 0, "the swap dropped it");
        Check.False(combat.ScannedTargets.Any(t => t.Id == mob), "and the scan is kept off it (its death animation)");
        Check.True(combat.HasTargets, "but it still counts as a target in range (corpse gates, nav hold)");
        Check.True(combat.HasCloseThreat(RynthAiPlugin.LootStarveCloseYards), "and as a close threat (no starved loot turn)");
        Check.True(combat.HasEngageableTarget, "so combat keeps the tick");

        Check.True(bot.HasLootWork(), "the corpse is lootable");
        bot.Tick(BotActivity.Combat);
        Check.Eq(bot.Get<int>("_targetCorpseId"), 0, "no corpse claimed while combat holds the tick");
        Check.True(FakeHost.Logs.Any(l => l.Contains($"corpse 0x{Corpse:X8} waits for combat")), "the corpse waits for combat, and says so");
        bot.Tick(BotActivity.Looting);
        Check.Eq(bot.Get<int>("_targetCorpseId"), 0, "nor with the Looting tick (the claim gate holds too)");
        Check.False(FakeHost.Uses.Contains(Corpse), "the corpse is not opened");
        AgeCorpse(bot, RynthAiPlugin.LootStarveMs + 1000);
        bot.HasLootWork();
        Check.False((bool)bot.Call("IsLootStarvedTurn", true, true)!, "a long-waiting corpse gets no starved turn past it either");

        // Its window passes with no word of its death: it is a target again at once.
        now = now.AddSeconds(2.1);
        Rescan(combat);
        Check.True(combat.ScannedTargets.Any(t => t.Id == mob), "after the window it is scanned again");
        Check.True(FakeHost.Logs.Any(l => l.Contains("not confirmed 2.1 s after the swap and it is still alive: it is a target again")),
                   "said once the window ends");
        combat.HandleCombatTrigger();
        Check.Eq(combat.activeTargetId, mob, "and combat locks it again");
    }

    private static void PredictedKillSettled()
    {
        DateTime now = new(2026, 10, 25, 19, 32, 58);
        int mob = unchecked((int)Monster);

        // The kill notice confirms it: the corpse may be looted.
        var bot = PredictedKillWorld(out var combat, () => now);
        combat.OnKillNotification("You killed the Ancient Water Elemental!");
        Check.False(combat.HasTargets, "kill notice: dead, nothing in range");
        Check.False(combat.HasEngageableTarget, "combat lets the tick go");
        Check.True(combat.IsKillSuppressed(mob), "the scan stays off it while its corpse forms");
        bot.HasLootWork();
        bot.Tick(BotActivity.Looting);
        Check.Eq(bot.Get<int>("_targetCorpseId"), unchecked((int)Corpse), "the corpse is claimed");

        // Health 0 from the server: dead too.
        bot = PredictedKillWorld(out combat, () => now);
        Check.True(combat.HasTargets, "pending");
        bot.Cache.OnUpdateHealth(Monster, 0f);   // the bot's cache is combat's world filter
        Check.False(combat.HasTargets, "health 0: dead, nothing in range");

        // A damage line on it after the swap (a killing blow sends none): alive, back in the scan now.
        PredictedKillWorld(out combat, () => now);
        combat.HandleChatForDamage("You singe Ancient Water Elemental for 7883 points with Incantation of Flame Bolt.", 7);
        Check.False(combat.IsKillSuppressed(mob), "the hit lifts the suppression");
        Check.True(FakeHost.Logs.Any(l => l.Contains("took a 7883-point hit after the swap")), "and says so");
        Rescan(combat);
        Check.True(combat.ScannedTargets.Any(t => t.Id == mob), "scanned again before the window ends");
        combat.HandleCombatTrigger();
        Check.Eq(combat.activeTargetId, mob, "combat goes back to it at once");
    }

    // ── Boost Loot Priority: one kill, one loot (2026-10-14, Kelpie: the next target was locked
    //    6 ms after each kill, the corpse came 3 s later, and corpses were looted in batches) ──

    /// <summary>
    /// A monster <paramref name="monsterMeters"/> away, Boost Loot Priority on, and combat's
    /// HoldNewTarget wired to the plugin. The corpse is created only with <paramref name="withCorpse"/>.
    /// </summary>
    private static Bot BoostWorld(float monsterMeters, bool withCorpse, out CombatManager combat, bool boost = true)
    {
        BaseWorld(corpsePositioned: true);
        FakeHost.Names[Monster] = "Ancient Water Elemental";
        FakeHost.ItemTypes[Monster] = ItemTypeCreature;
        FakeHost.Positions[Monster] = (Me0.Cell, Me0.X + monsterMeters, Me0.Y + 0.5f, Me0.Z);
        var host = FakeHost.Create(Player);
        var bot = new Bot(host);
        bot.S.EnableCombat = true;
        bot.S.MonsterRange = 50;
        bot.S.BoostLootPriority = boost;
        if (withCorpse) bot.Cache.OnCreateObject(Corpse);
        _ = bot.Cache[unchecked((int)Monster)];
        bot.Cache.OnUpdateHealth(Monster, 1f);
        bot.Cache.Tick();
        combat = new CombatManager(host, bot.S, bot.Cache);
        combat.SetPlayerId(Player);
        typeof(CombatManager).GetField("_lastScanTime", Any)?.SetValue(combat, DateTime.MinValue);
        combat.ScanNearbyTargets();
        bot.Set("_combatManager", combat);
        combat.HoldNewTarget = () => (bool)bot.Call("BoostLootHoldsNewTarget")!;
        return bot;
    }

    private static void BoostLootOneKillOneLoot()
    {
        var bot = BoostWorld(15f, withCorpse: false, out var combat);
        Check.False((bool)bot.Call("BoostLootHoldsNewTarget")!, "no kill, no corpse: nothing to wait for");

        // The kill lands; the corpse doesn't exist yet (ACE creates it after the death animation).
        bot.Call("NoteBoostLootKill");
        combat.HandleCombatTrigger();
        Check.Eq(combat.activeTargetId, 0, "the next monster (15 m) is not locked while the corpse forms");
        Check.True(FakeHost.Logs.Any(l => l.Contains("[CombatTarget] next target waits: Boost Loot Priority")), "said once");
        Check.True(bot.HasLootWork(), "the forming corpse is loot work");
        Check.False(combat.IsUnderCloseAttack, "nothing locked, nothing close: not engaged");
        var waiting = new ArbiterInputs(true, false, combat.HasEngageableTarget, true, false, true,
                                        combatEngaged: combat.IsUnderCloseAttack, boostLoot: true);
        Check.Eq(ActivityArbiter.Decide(in waiting), BotActivity.Looting, "so the arbiter gives Looting the tick");

        // The corpse arrives: it is claimed and opened before the next fight.
        bot.Cache.OnCreateObject(Corpse);
        bot.Cache.Tick();
        Check.True(bot.HasLootWork(), "the kill's corpse is lootable");
        combat.HandleCombatTrigger();
        Check.Eq(combat.activeTargetId, 0, "still no new target with the corpse in range");
        bot.Tick(BotActivity.Looting);
        Check.Eq(bot.Get<int>("_targetCorpseId"), unchecked((int)Corpse), "the corpse is claimed");
        Check.True(FakeHost.Uses.Contains(Corpse), "and opened");
    }

    private static void BoostLootHoldLimits()
    {
        // The corpse never comes: the wait runs out and the next monster is locked.
        var bot = BoostWorld(15f, withCorpse: false, out var combat);
        bot.Call("NoteBoostLootKill");
        bot.Set("_boostLootKillAt", Environment.TickCount64 - RynthAiPlugin.BoostLootKillWaitMs - 1);
        Check.False((bool)bot.Call("BoostLootHoldsNewTarget")!, "past BoostLootKillWaitMs: no hold");
        combat.HandleCombatTrigger();
        Check.Eq(combat.activeTargetId, unchecked((int)Monster), "the next monster is locked");

        // A monster within 5 yd is fought at once.
        var close = BoostWorld(2f, withCorpse: false, out var combat2);
        close.Call("NoteBoostLootKill");
        combat2.HandleCombatTrigger();
        Check.Eq(combat2.activeTargetId, unchecked((int)Monster), "a monster at 2 m: locked, the corpse waits");

        // Boost Loot off: the old order (combat first) is unchanged.
        var off = BoostWorld(15f, withCorpse: false, out var combat3, boost: false);
        off.Call("NoteBoostLootKill");
        Check.False(off.HasLootWork(), "no forming-corpse loot work without Boost Loot");
        combat3.HandleCombatTrigger();
        Check.Eq(combat3.activeTargetId, unchecked((int)Monster), "the next monster is locked at once");

        // Rare-only looting: most kills leave nothing to wait for.
        var rare = BoostWorld(15f, withCorpse: false, out var combat4);
        rare.S.LootOnlyRareCorpses = true;
        rare.Call("NoteBoostLootKill");
        combat4.HandleCombatTrigger();
        Check.Eq(combat4.activeTargetId, unchecked((int)Monster), "Loot Only Rare Corpses: no wait");

        // Loot Mode rares (rares and needed mana stones only): the same.
        var raresMode = BoostWorld(15f, withCorpse: false, out var combat5);
        raresMode.S.LootMode = RynthCore.Plugin.RynthAi.Loot.LootModes.Rares;
        raresMode.Call("NoteBoostLootKill");
        combat5.HandleCombatTrigger();
        Check.Eq(combat5.activeTargetId, unchecked((int)Monster), "Loot Mode rares: no wait");
    }

    private static void BoostLootWaitEndsOnKillCorpse()
    {
        var bot = BoostWorld(15f, withCorpse: false, out _);
        var waiting = bot.Get<Dictionary<int, long>>("_corpseWaitingSince");
        const int Older = 0x0BADC0DE, Fresh = 0x0BADF00D;

        bot.Call("NoteBoostLootKill");
        long kill = bot.Get<long>("_boostLootKillAt");
        waiting[Older] = kill - 5000;
        bot.Call("EndBoostLootKillWait", Older);
        Check.Eq(bot.Get<long>("_boostLootKillAt"), kill, "a corpse from an earlier kill doesn't end the wait");

        waiting[Fresh] = kill + 1500;
        bot.Call("EndBoostLootKillWait", Fresh);
        Check.Eq(bot.Get<long>("_boostLootKillAt"), 0L, "the kill's corpse (first seen after it) ends the wait");
        Check.False((bool)bot.Call("BoostLootHoldsNewTarget")!, "so combat goes on at once");
    }

    private static void HeldByCombatLoggedOnce()
    {
        var bot = StarvationWorld(15f, out _);
        bot.HasLootWork();
        for (int i = 0; i < 5; i++) bot.Tick(BotActivity.Combat);
        int lines = FakeHost.Logs.Count(l => l.Contains($"corpse 0x{Corpse:X8} waits for combat"));
        Check.Eq(lines, 1, "one line for the held corpse, not one per tick");
    }

    // ── Closing the corpse when done (2026-10-04: "when finished looting I don't think the
    //    corpse closes"). API v76 CloseContainer sends the client's own close packet (0x0195). ──

    /// <summary>Claims, opens and starts looting the corpse; the gem's move has been sent.
    /// With <paramref name="closeSupported"/> the host has CloseContainer and GetGroundContainerId,
    /// and the client reports the corpse open (as the engine does after ViewContents).</summary>
    private static Bot OpenCorpseAndSendPickup(bool closeSupported)
    {
        BaseWorld(corpsePositioned: true);
        FakeHost.CloseContainerCalls = closeSupported;
        var host = FakeHost.Create(Player);
        Check.Eq(host.HasCloseContainer, closeSupported, "the host's CloseContainer support");
        var bot = new Bot(host);
        bot.Cache.OnCreateObject(Corpse);
        bot.Cache.Tick();
        bot.Tick(BotActivity.Looting);
        Check.True(FakeHost.Uses.Contains(Corpse), "the corpse is opened");

        bot.Cache.OnCreateObject(Gem);
        bot.Cache.Tick();
        if (closeSupported)
            FakeHost.GroundContainer = Corpse;
        bot.P.OnViewObjectContents(Corpse);
        for (int i = 0; i < 100 && !FakeHost.ExternalMoves.Any(m => m.Id == Gem); i++)
        {
            bot.Tick(BotActivity.Looting);
            Thread.Sleep(10);
        }
        Check.True(FakeHost.ExternalMoves.Contains((Gem, Player, 0)), "the gem's move is sent");
        Check.Eq(FakeHost.Closes.Count, 0, "nothing closed while an item is still being looted");
        return bot;
    }

    private static bool Completed(Bot bot) =>
        bot.Get<Dictionary<int, long>>("_completedCorpses").ContainsKey(unchecked((int)Corpse));

    /// <summary>The gem lands in the pack; ticks until the corpse is marked completed.</summary>
    private static void FinishLooting(Bot bot)
    {
        FakeHost.Containers[Gem] = Player;
        for (int i = 0; i < 200 && !Completed(bot); i++)
        {
            bot.Tick(BotActivity.Looting);
            Thread.Sleep(10);
        }
        Check.True(Completed(bot), "the corpse is completed once its last item is in the pack");
    }

    private static void FinishedCorpseClosedOnce()
    {
        var bot = OpenCorpseAndSendPickup(closeSupported: true);
        FinishLooting(bot);
        Check.Eq(string.Join(",", FakeHost.Closes.Select(c => $"{c:X8}")), $"{Corpse:X8}", "the finished corpse is closed, once");
        Check.Eq(bot.Get<int>("_busyCount"), 0, "the close adds nothing to the busy count");
        Check.Eq(bot.Get<int>("_openedContainerId"), 0, "the bot no longer holds it open");

        // Until the server's answer lands the client still reports it open: no second close.
        for (int i = 0; i < 5; i++) bot.Tick(BotActivity.Looting);
        Check.Eq(FakeHost.Closes.Count, 1, "no re-close while the client still shows it");
        Check.Eq(bot.Get<int>("_openedContainerId"), 0, "a completed corpse is not re-claimed from the client's state");

        // The server's CloseGroundContainer: the window goes, StopViewingObjectContents follows.
        FakeHost.GroundContainer = 0;
        bot.P.OnStopViewingObjectContents(Corpse);
        for (int i = 0; i < 5; i++) bot.Tick(BotActivity.Looting);
        Check.Eq(FakeHost.Closes.Count, 1, "still one close after the server's answer");
        Check.Eq(FakeHost.Uses.Count(u => u == Corpse), 1, "never a UseObject to close it");
    }

    private static void FinishedCorpseNotClosedOnOldEngine()
    {
        var bot = OpenCorpseAndSendPickup(closeSupported: false);
        FinishLooting(bot);
        Check.Eq(FakeHost.Closes.Count, 0, "no CloseContainer on an engine before v76");
        Check.Eq(FakeHost.Uses.Count(u => u == Corpse), 1, "and no UseObject to close it either");
    }

    private static void AbandonedCorpseClosed()
    {
        var bot = OpenCorpseAndSendPickup(closeSupported: true);
        bot.Call("AbandonCurrentCorpse", Environment.TickCount64, "timeout");
        Check.Eq(string.Join(",", FakeHost.Closes.Select(c => $"{c:X8}")), $"{Corpse:X8}", "the abandoned corpse is closed, once");
        Check.True(Completed(bot), "an opened corpse that timed out is completed (as before)");
        Check.Eq(bot.Get<int>("_openedContainerId"), 0, "and released");
    }

    private static void MacroStopClosesCorpse()
    {
        var bot = OpenCorpseAndSendPickup(closeSupported: true);
        bot.S.IsMacroRunning = false;
        bot.Call("CloseOpenCorpseOnMacroStop");
        Check.Eq(string.Join(",", FakeHost.Closes.Select(c => $"{c:X8}")), $"{Corpse:X8}", "the open corpse is closed when the macro stops");
        Check.False(Completed(bot), "not completed: a later run can loot what is left");

        // Macro off, ticking on: no further close before the server's answer.
        for (int i = 0; i < 5; i++) bot.Tick(BotActivity.Idle);
        Check.Eq(FakeHost.Closes.Count, 1, "one close");
        FakeHost.GroundContainer = 0;
        bot.P.OnStopViewingObjectContents(Corpse);
        Check.Eq(bot.Get<int>("_openedContainerId"), 0, "the server's answer clears the open corpse");

        // No open corpse: nothing to close.
        bot.Call("CloseOpenCorpseOnMacroStop");
        Check.Eq(FakeHost.Closes.Count, 1, "a stop with no corpse open closes nothing");
    }

    // 2026-10-07, Drakkon: picking up an item plays a bend-down, and the engine holds queued
    // moves until it ends (about 2.2 s). RynthAi retried every 0.5 s from the send and gave up
    // after 3 tries, so items were skipped while their first move had not even gone out.
    private static void PickupWaitsForAnimation()
    {
        BaseWorld(corpsePositioned: true);
        FakeHost.ServerGates = true; // GetCastBusyState
        var host = FakeHost.Create(Player);
        var bot = new Bot(host);
        bot.Cache.OnCreateObject(Corpse);
        bot.Cache.Tick();
        bot.Tick(BotActivity.Looting);
        Check.True(FakeHost.Uses.Contains(Corpse), "the corpse is opened");

        // The last pickup is still animating when the corpse's contents arrive.
        FakeHost.CastBusy = 1;
        bot.Cache.OnCreateObject(Gem);
        bot.Cache.Tick();
        bot.P.OnViewObjectContents(Corpse);
        TickFor(bot, 500);
        Check.False(FakeHost.ExternalMoves.Any(m => m.Id == Gem), "no pickup sent while the character animates");

        FakeHost.CastBusy = 0;
        for (int i = 0; i < 100 && !FakeHost.ExternalMoves.Any(m => m.Id == Gem); i++)
        {
            bot.Tick(BotActivity.Looting);
            Thread.Sleep(10);
        }
        Check.Eq(FakeHost.ExternalMoves.Count(m => m.Id == Gem), 1, "the pickup goes out once the character is still");

        // The bot's Loot Retry Timeout (the LootBaseline default).
        int retryWindowMs = bot.S.LootRetryTimeoutMs;

        // Its own bend-down plays for longer than 3 retry windows: no retry, no give-up.
        FakeHost.CastBusy = 1;
        TickFor(bot, retryWindowMs * 3 + 300);
        Check.Eq(FakeHost.ExternalMoves.Count(m => m.Id == Gem), 1, "no retry while the pickup animates");
        Check.False(FakeHost.Logs.Any(l => l.Contains("pickup failed")), "the item is not given up on");

        // Still again and the item still on the corpse: the retry window starts now.
        FakeHost.CastBusy = 0;
        TickFor(bot, retryWindowMs / 2);
        Check.Eq(FakeHost.ExternalMoves.Count(m => m.Id == Gem), 1, "the retry window counts from when the character is still");
        // (Also past the 4 s answer wait from the send by now: R-20261029-122319-321f.)
        TickFor(bot, retryWindowMs);
        Check.Eq(FakeHost.ExternalMoves.Count(m => m.Id == Gem), 2, "then one retry");
    }

    // 2026-10-15, Drakkon: every item went to one pack the server wouldn't take loot into (its
    // contents unknown to the client), 3 tries each, and every item on every corpse was skipped.
    // A pack that takes none of an item's tries now counts as full for a while: the item gets one
    // more round into another pack, and with none left looting stops with the pack-full message.
    // 2026-10-29 (R-20261029-122319-321f): only the server's refusals mark a pack full, so the
    // refusals are given here (unanswered tries: LootRetryTimingTests).
    private static void RefusingPackIsLeftOut()
    {
        var refusals = new RynthCore.Plugin.RynthAi.Loot.PackRefusals();
        refusals.Refuse(7, 1000);
        Check.True(refusals.IsRefused(7, 1000 + RynthCore.Plugin.RynthAi.Loot.PackRefusals.HoldMs - 1), "refused for the hold");
        Check.False(refusals.IsRefused(7, 1000 + RynthCore.Plugin.RynthAi.Loot.PackRefusals.HoldMs), "then tried again");
        refusals.Refuse(8, 0);
        refusals.Accepted(8);
        Check.False(refusals.IsRefused(8, 1), "an item landing there ends the hold");

        BaseWorld(corpsePositioned: true);
        FakeHost.WeenieErrorCalls = true;   // the server's refusals (2026-10-29: only they mark a pack full)
        var host = FakeHost.Create(Player);
        var bot = new Bot(host);
        bot.Cache.OnCreateObject(Corpse);
        bot.Cache.Tick();
        bot.Tick(BotActivity.Looting);
        bot.Cache.OnCreateObject(Gem);
        bot.Cache.Tick();
        bot.P.OnViewObjectContents(Corpse);

        // The main pack (the only destination here) never takes the gem: it stays on the corpse,
        // and the server refuses each move once its pickup has run (~0.8 s: "Unable to put X into
        // container" + InventoryServerSaveFailed with no error code).
        int seen = 0;
        var refuseAt = new List<long>();
        long end = Environment.TickCount64 + 6000;
        while (Environment.TickCount64 < end && !FakeHost.Logs.Any(l => l.Contains("no pack has a free slot")))
        {
            for (; seen < FakeHost.ExternalMoves.Count; seen++)
                if (FakeHost.ExternalMoves[seen].Id == Gem) refuseAt.Add(Environment.TickCount64 + 800);
            while (refuseAt.Count > 0 && Environment.TickCount64 >= refuseAt[0])
            {
                refuseAt.RemoveAt(0);
                FakeHost.WeenieError(0, 0x00A0, Gem);
            }
            bot.Tick(BotActivity.Looting);
            Thread.Sleep(10);
        }
        Check.Eq(FakeHost.ExternalMoves.Count(m => m.Id == Gem), 3, "three tries into the main pack, each after the last one's refusal");
        Check.True(FakeHost.ExternalMoves.Where(m => m.Id == Gem).All(m => m.Target == Player), "all into the main pack");
        Check.True(FakeHost.Logs.Any(l => l.Contains("took none of 3 tries") && l.Contains("trying another pack")), "the refusing pack is left out, said once");
        Check.True(FakeHost.Logs.Any(l => l.Contains("no pack has a free slot")), "no other pack: the corpse is left");
        Check.True(FakeHost.Chat.Any(l => l.Contains("Pack full")), "and the player is told");
        Check.False(FakeHost.Logs.Any(l => l.Contains("pickup failed")), "not three more wasted tries into the same pack");
    }

    // A pickup sent while Bloodstone Bolt VII was unanswered came back
    // "too busy" (ACE refuses a pickup while IsBusy); the assess-timeout path skips the busy gate.
    private static void PickupWaitsForCastAnswer()
    {
        BaseWorld(corpsePositioned: true);
        FakeHost.ServerGates = true; // GetCastBusyState, GetUseDoneSeq
        var host = FakeHost.Create(Player);
        var bot = new Bot(host);
        long t = 80_000_000;
        ActionPacer.Reset(); ActionPacer.Enabled = true; ActionPacer.NowMs = () => t;
        try
        {
            bot.Cache.OnCreateObject(Corpse);
            bot.Cache.Tick();
            bot.Tick(BotActivity.Looting);
            Check.True(FakeHost.Uses.Contains(Corpse), "the corpse is opened");
            ActionPacer.Reset(); ActionPacer.Enabled = true; ActionPacer.NowMs = () => t;   // the open is answered

            // A cast goes out (Combat) and has no answer yet when the contents arrive.
            ActionPacer.Begin(host, PacedKind.Cast, 0, "Bloodstone Bolt VII", "Combat", FakeHost.UseDoneSeq);
            bot.Cache.OnCreateObject(Gem);
            bot.Cache.Tick();
            bot.P.OnViewObjectContents(Corpse);
            for (int i = 0; i < 10; i++) { t += 50; bot.Tick(BotActivity.Looting); Thread.Sleep(10); }
            Check.False(FakeHost.ExternalMoves.Any(m => m.Id == Gem), "no pickup while the cast is unanswered");
            Check.Eq(FakeHost.Logs.Count(l => l.Contains("pickup waits - the server still owes an answer")), 1, "the hold is logged once");

            FakeHost.UseDone(0);   // the cast's answer
            for (int i = 0; i < 100 && !FakeHost.ExternalMoves.Any(m => m.Id == Gem); i++) { t += 50; bot.Tick(BotActivity.Looting); Thread.Sleep(10); }
            Check.Eq(FakeHost.ExternalMoves.Count(m => m.Id == Gem), 1, "the pickup goes on the cast's UseDone");

            // The item stays on the corpse (no answer); the next cast is unanswered when a retry is
            // due (after the answer wait, 2026-10-29: R-20261029-122319-321f).
            ActionPacer.Begin(host, PacedKind.Cast, 0, "Bloodstone Bolt I", "Combat", FakeHost.UseDoneSeq);
            TickFor(bot, (int)RynthAiPlugin.PickupAnswerWaitMinMs + bot.S.LootRetryTimeoutMs + 500);
            Check.Eq(FakeHost.ExternalMoves.Count(m => m.Id == Gem), 1, "no retry while a cast is unanswered");
            FakeHost.UseDone(0);
            TickFor(bot, 300);
            Check.Eq(FakeHost.ExternalMoves.Count(m => m.Id == Gem), 2, "the retry goes once it is answered");
        }
        finally { ActionPacer.Enabled = false; ActionPacer.Reset(); ActionPacer.NowMs = () => Environment.TickCount64; }
    }
    // 2026-10-07: the client takes an item off the corpse list as soon as the move is sent; the
    // corpse was closed then, and the character picked the item up after the corpse closed.
    private static void CloseWaitsForInventory()
    {
        var bot = OpenCorpseAndSendPickup(closeSupported: true);
        FakeHost.Containers[Gem] = 0x7FFFFFF0;   // off the corpse, not in a pack yet
        TickFor(bot, 1500);
        Check.Eq(FakeHost.Closes.Count, 0, "not closed while the item is on its way");
        Check.False(Completed(bot), "not completed either");

        FakeHost.Containers[Gem] = Player;       // in the main pack
        for (int i = 0; i < 200 && FakeHost.Closes.Count == 0; i++)
        {
            bot.Tick(BotActivity.Looting);
            Thread.Sleep(10);
        }
        Check.Eq(FakeHost.Closes.Count, 1, "closed once the item is in the inventory");
        Check.True(FakeHost.Logs.Any(l => l.Contains("pickup confirmed")), "and the pickup confirmed");
    }

    // 2026-10-11, Silentkelpie: Necrolytic Extracts and Coalesced Aetheria merge into stacks
    // already in the pack (StackableMerge). The server removes the picked object and the client
    // keeps no container for it, so it never "arrived" in the inventory and every such item
    // waited out PickupArriveMaxMs (4 s) before the next pickup. With the 12 s corpse timeout,
    // a corpse with three or four keeps was closed part way through.
    private const uint Gem2 = 0x80261102, Gem3 = 0x80261103;

    private static Bot ThreeGemCorpse(int corpseTimeoutMs)
    {
        BaseWorld(corpsePositioned: true);
        foreach (uint g in new[] { Gem2, Gem3 })
        {
            FakeHost.Names[g] = "Black Opal";
            FakeHost.ItemTypes[g] = ItemTypeGem;
            FakeHost.Containers[g] = Corpse;
            FakeHost.Appraised.Add(g);
        }
        // Stackable, as the real merging items are: only a stack that ends up with no container
        // counts as merged (an unknown item off the corpse with no container is still on its way).
        foreach (uint g in new[] { Gem, Gem2, Gem3 })
        {
            FakeHost.Ints[(g, 11)] = 100;   // MaxStackSize
            FakeHost.Ints[(g, 12)] = 1;     // StackCount
        }
        FakeHost.CloseContainerCalls = true;
        var host = FakeHost.Create(Player);
        var bot = new Bot(host);
        bot.S.LootCorpseTimeoutMs = corpseTimeoutMs;
        bot.Cache.OnCreateObject(Corpse);
        bot.Cache.Tick();
        bot.Tick(BotActivity.Looting);
        Check.True(FakeHost.Uses.Contains(Corpse), "the corpse is opened");
        foreach (uint g in new[] { Gem, Gem2, Gem3 })
            bot.Cache.OnCreateObject(g);
        bot.Cache.Tick();
        FakeHost.GroundContainer = Corpse;
        bot.P.OnViewObjectContents(Corpse);
        return bot;
    }

    /// <summary>Ticks for up to <paramref name="ms"/>; every item whose move went out merges at once.</summary>
    private static long LootWithMerges(Bot bot, int ms, int wantMoves)
    {
        long start = Environment.TickCount64, end = start + ms;
        while (Environment.TickCount64 < end && FakeHost.ExternalMoves.Select(m => m.Id).Distinct().Count() < wantMoves)
        {
            foreach (var m in FakeHost.ExternalMoves)
                FakeHost.Containers[m.Id] = 0;   // merged: gone from the corpse, no container
            bot.Tick(BotActivity.Looting);
            Thread.Sleep(10);
        }
        foreach (var m in FakeHost.ExternalMoves)
            FakeHost.Containers[m.Id] = 0;
        return Environment.TickCount64 - start;
    }

    private static void MergedPickupConfirmedQuickly()
    {
        var bot = ThreeGemCorpse(corpseTimeoutMs: 60_000);
        long took = LootWithMerges(bot, 6000, wantMoves: 2);
        Check.Eq(FakeHost.ExternalMoves.Select(m => m.Id).Distinct().Count(), 2, "the second pickup goes out");
        Check.True(took < 2800, $"the second pickup follows the first merge in about the settle time, not 4 s more (took {took} ms)");
        Check.True(FakeHost.Logs.Any(l => l.Contains("merged into a stack in the pack")), "the confirm says why");
    }

    private static void ManyKeepsBeatTheTimeout()
    {
        // Each merged item takes about PickupSettleMs (1 s) to confirm; three in a row take longer
        // than this corpse timeout, which only an idle corpse should hit.
        var bot = ThreeGemCorpse(corpseTimeoutMs: 2200);
        LootWithMerges(bot, 8000, wantMoves: 3);
        TickFor(bot, 1500);
        var moved = FakeHost.ExternalMoves.Select(m => m.Id).Distinct().ToList();
        Check.Eq(moved.Count, 3, "all three gems are taken (moved: " + string.Join(",", moved.Select(i => $"{i:X8}")) + ")");
        Check.False(FakeHost.Logs.Any(l => l.Contains("abandoning corpse")), "the corpse is not abandoned on the timeout");
    }

    private static void TickFor(Bot bot, int ms)
    {
        long end = Environment.TickCount64 + ms;
        while (Environment.TickCount64 < end)
        {
            bot.Tick(BotActivity.Looting);
            Thread.Sleep(10);
        }
    }
}

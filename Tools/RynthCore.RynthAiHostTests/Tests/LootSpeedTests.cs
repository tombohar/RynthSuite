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

// 2026-10-12, DreamWeave "Thread" caches (~100 items, ~20 keepers) took ~50 s to loot:
//  1. A stack that merges into one already in the pack is deleted by the server (ACE
//     StackableMerge: InventoryRemoveObject), so it never shows up in the inventory, and the
//     pickup waited PickupArriveMaxMs (4 s) for it: 4.8 s instead of ~1.5 s per stack.
//  2. A decided item waited behind the assess window's busy-count gate.
//  3. Every move went out only after the last pickup's Return had played and the client had
//     seen the character still. ACE queues a move that arrives during the Return (NextPickup,
//     one ahead), so the next move now goes out at the answer.
//  4. Matched items were re-judged and re-logged before every pickup; the mana stone cap line
//     was printed per stone.
// The ACE side is simulated (AceSim): pickup motion, item moved, Return; a move during the
// Return is queued, one during the pickup motion is "You're too busy"; and the client's own
// one-request-at-a-time slot (a move sent while the last is unanswered never leaves the client).
internal static class LootSpeedTests
{
    private const uint Player = 0x50000D01;
    private const uint Chest = 0x7A9B5001;
    private const uint ItemTypeContainer = 0x200, ItemTypeGem = 0x800, ItemTypeMisc = 0x80, ItemTypeArmor = 0x2;
    private const uint KeyMaxStack = 11, KeyStack = 12, KeyArmorLevel = 28;
    private const BindingFlags Any = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
    private const long TickMs = 33;

    public static void Register(Runner r)
    {
        r.Add("loot speed: a stack that merges into a pack stack is confirmed at the server's answer, not after 4 s", MergedStackConfirmsAtAnswer);
        r.Add("loot speed: a decided item is picked up while the other IDs are still coming in", DecidedItemBeforeIds);
        r.Add("loot speed: the next move goes out when the last item leaves the chest (queued behind its Return), one at a time", NextMoveAtAnswer);
        r.Add("loot speed: a 'You're too busy' answer is sent again once the character is still, without using up a try", TooBusyRetried);
        r.Add("loot speed: the chest closes only after the last pickup's Return has played", CloseWaitsForLastReturn);
        r.Add("loot speed: a 100-item chest with 20 keepers (6 merging stacks) against simulated ACE timings", FullChestTiming);
        r.Add("loot speed: a matched item's [LootEval] line is printed once per open, not before every pickup", LootEvalLoggedOnce);
    }

    // ── Fake clock ────────────────────────────────────────────────────────────

    private static long _t;

    private static T WithClock<T>(Func<T> body)
    {
        var saved = RynthAiPlugin.CorpseClock;
        _t = 10_000_000;
        RynthAiPlugin.CorpseClock = () => _t;
        try { return body(); }
        finally { RynthAiPlugin.CorpseClock = saved; }
    }

    private static void WithClock(Action body) => WithClock(() => { body(); return 0; });

    // ── World ─────────────────────────────────────────────────────────────────

    private static (uint Cell, float X, float Y, float Z) Me0 => (0x1048002Au, 100f, 100f, 0f);

    private static void BaseWorld()
    {
        FakeHost.Reset();
        FakeHost.SalvageCalls = true;        // UseObject + MoveItemExternal
        FakeHost.WeaponCalls = true;         // RequestId, HasAppraisalData
        FakeHost.CloseContainerCalls = true; // CloseContainer + GetGroundContainerId
        FakeHost.ServerGates = true;         // GetCastBusyState (the pickup motions)
        FakeHost.WeenieErrorCalls = true;    // GetLastWeenieError ("You're too busy")
        FakeHost.Names[Player] = "Dargoth Hera";
        FakeHost.Positions[Player] = Me0;
        FakeHost.PlayerPose = Me0;
        FakeHost.Names[Chest] = "Drakkon's Thread Cache";
        FakeHost.ItemTypes[Chest] = ItemTypeContainer;
        FakeHost.Positions[Chest] = (Me0.Cell, Me0.X + 1.0f, Me0.Y, Me0.Z);
        FakeHost.NotAttackable.Add(Chest);
        FakeHost.Containers[Chest] = 0;
    }

    private static void AddItem(uint id, string name, uint type, bool appraised = true, bool stack = false)
    {
        FakeHost.Names[id] = name;
        FakeHost.ItemTypes[id] = type;
        FakeHost.Containers[id] = Chest;
        if (appraised) FakeHost.Appraised.Add(id);
        if (stack)
        {
            FakeHost.Ints[(id, KeyMaxStack)] = 100;
            FakeHost.Ints[(id, KeyStack)] = 1;
        }
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
        public AceSim? Sim;

        public Bot(RynthCoreHost host, bool armorStatRule = false)
        {
            typeof(RynthPluginBase).GetMethod("Attach", Any)!.Invoke(P, new object[] { FakeHost.LastApi });
            var dash = (LegacyDashboardRenderer)RuntimeHelpers.GetUninitializedObject(typeof(LegacyDashboardRenderer));
            typeof(LegacyDashboardRenderer).GetField("_settings", Any)!.SetValue(dash, S);
            Set("_dashboard", dash);
            Cache = new WorldObjectCache(host);
            Cache.SetPlayerId(Player);
            Set("_objectCache", Cache);
            Set("_playerId", Player);

            string profile = Path.Combine(Program.TempRoot, armorStatRule ? "loot-speed-armor.json" : "loot-speed.json");
            var p = new LootProfile { Name = "speed" };
            var gems = new LootRule { Name = "Gems", Action = LootAction.Keep };
            gems.Conditions.Add(new ObjectClassCondition { ObjectClass = AcObjectClass.Gem });
            p.Rules.Add(gems);
            var misc = new LootRule { Name = "Peas", Action = LootAction.Keep };
            misc.Conditions.Add(new ObjectClassCondition { ObjectClass = AcObjectClass.Misc });
            p.Rules.Add(misc);
            if (armorStatRule)
            {
                // A stat rule: armor is judged only with its appraisal.
                var armor = new LootRule { Name = "AL 300 armor", Action = LootAction.Keep };
                armor.Conditions.Add(new ObjectClassCondition { ObjectClass = AcObjectClass.Armor });
                armor.Conditions.Add(new LongValKeyGECondition { Key = (int)KeyArmorLevel, Value = 300 });
                p.Rules.Add(armor);
            }
            p.Save(profile);
            S.CurrentLootPath = profile;
        }

        public void Set(string f, object? v) => typeof(RynthAiPlugin).GetField(f, Any)!.SetValue(P, v);
        public T Get<T>(string f) => (T)typeof(RynthAiPlugin).GetField(f, Any)!.GetValue(P)!;

        public void Create(IEnumerable<uint> ids)
        {
            var list = ids.ToList();
            foreach (uint id in list) Cache.OnCreateObject(id);
            // The cache classifies new objects on a per-tick time budget: tick it until every item
            // is filed (in game this happens during the CreateObject burst, before the open).
            int items = list.Count(i => i != Chest);
            for (int i = 0; i < 200; i++)
            {
                Cache.Tick();
                if (Cache.GetContainedItems(unchecked((int)Chest)).Count() >= items) break;
            }
        }

        public void ServerOpens(uint container)
        {
            FakeHost.GroundContainer = container;
            P.OnViewObjectContents(container);
            P.OnUpdateObjectInventory(container);
        }

        /// <summary>One plugin tick: the clock moves on TickMs, the simulated server catches up, the looter runs.</summary>
        public void Tick()
        {
            _t += TickMs;
            Sim?.Step(_t);
            Set("_activity", BotActivity.Looting);
            typeof(RynthAiPlugin).GetMethod("TickCorpseOpening", Any)!.Invoke(P, null);
        }

        public void TickUntil(Func<bool> done, long maxMs)
        {
            long end = _t + maxMs;
            while (_t < end && !done()) Tick();
        }

        public void TickFor(long ms)
        {
            long end = _t + ms;
            while (_t < end) Tick();
        }

        public int MovesOf(uint id) => FakeHost.ExternalMoves.Count(m => m.Id == id);
    }

    /// <summary>
    /// ACE's pickup (Player_Inventory.cs: StartPickup, the pickup motion, DoHandleActionPutItemInContainer
    /// or StackableMerge, EnqueuePickupDone's Return, NextPickup) and the client's pending-request slot.
    /// Server events run at server time; what the client sees of them lands Rtt/2 later.
    /// </summary>
    private sealed class AceSim
    {
        public long Rtt = 40, PickupMs = 740, ReturnMs = 740;
        public readonly HashSet<uint> Mergers = new();   // stacks that merge into a pack stack
        public readonly HashSet<uint> Junk = new();      // FakeHost.Containers keys never moved
        public bool KeepNameOnMerge;                     // the client still reads the merged object's name
        public int TooBusy, LocalRefused, Queued;
        public readonly List<(uint Id, long Answer)> Answers = new();

        private enum St { Idle, Pickup, Return }
        private St _st;
        private uint _next;
        private int _seenMoves;
        private uint _clientPending;
        private readonly List<(long At, int Seq, Action Act)> _events = new();
        private int _seq;

        private void At(long at, Action act) => _events.Add((at, _seq++, act));

        public void Step(long now)
        {
            // Moves the client sent since the last step (sent "now": the plugin tick before).
            while (_seenMoves < FakeHost.ExternalMoves.Count)
            {
                uint id = FakeHost.ExternalMoves[_seenMoves++].Id;
                if (_clientPending != 0) { LocalRefused++; continue; }   // "You can only move or use one item at a time"
                _clientPending = id;
                long arrive = now - TickMs + Rtt / 2;
                At(arrive, () => Receive(id, arrive));
            }
            while (true)
            {
                var due = _events.Where(e => e.At <= now).OrderBy(e => e.At).ThenBy(e => e.Seq).ToList();
                if (due.Count == 0) break;
                var e = due[0];
                _events.Remove(e);
                e.Act();
            }
        }

        private void Receive(uint id, long ts)
        {
            if (_st == St.Idle) { StartPickup(id, ts); return; }
            if (_st == St.Return && _next == 0) { _next = id; Queued++; return; }
            TooBusy++;
            At(ts + Rtt / 2, () =>
            {
                FakeHost.WeenieError(0x1D);               // YoureTooBusy
                FakeHost.WeenieError(0, 0x00A0, id);      // InventoryServerSaveFailed(item)
                if (_clientPending == id) _clientPending = 0;
            });
        }

        private void StartPickup(uint id, long ts)
        {
            _st = St.Pickup;
            At(ts + Rtt / 2, () => FakeHost.CastBusy = 1);
            At(ts + PickupMs, () => MoveItem(id, ts + PickupMs));
        }

        private void MoveItem(uint id, long ts)
        {
            _st = St.Return;
            At(ts + Rtt / 2, () =>
            {
                if (Mergers.Contains(id))
                {
                    // InventoryRemoveObject: the client drops the object; a pack stack grows.
                    FakeHost.Containers.Remove(id);
                    if (!KeepNameOnMerge) FakeHost.Names.Remove(id);
                    FakeHost.Positions.Remove(id);
                }
                else
                    FakeHost.Containers[id] = Player;
                if (_clientPending == id) _clientPending = 0;
                Answers.Add((id, ts + Rtt / 2));
            });
            At(ts + ReturnMs, () => ReturnDone(ts + ReturnMs));
        }

        private void ReturnDone(long ts)
        {
            if (_next != 0)
            {
                uint n = _next;
                _next = 0;
                StartPickup(n, ts);   // the queued move runs the moment the Return ends
                return;
            }
            _st = St.Idle;
            At(ts + Rtt / 2, () => FakeHost.CastBusy = 0);
        }
    }

    private static Bot OpenChest(IEnumerable<uint> items, bool armorStatRule = false, AceSim? sim = null)
    {
        var host = FakeHost.Create(Player);
        var bot = new Bot(host, armorStatRule) { Sim = sim };
        bot.Create(new[] { Chest }.Concat(items));
        bot.ServerOpens(Chest);
        return bot;
    }

    private static string Log(string needle) => FakeHost.Logs.FirstOrDefault(l => l.Contains(needle)) ?? "";

    // ── 1. Merged stack ───────────────────────────────────────────────────────

    private static void MergedStackConfirmsAtAnswer()
    {
        MergedStack(keepName: false);
        MergedStack(keepName: true);   // owner gone, name still readable: the stack flag decides
    }

    private static void MergedStack(bool keepName) => WithClock(() =>
    {
        BaseWorld();
        const uint Pea = 0x80264001;
        AddItem(Pea, "Silver Pea", ItemTypeMisc, stack: true);
        var sim = new AceSim { KeepNameOnMerge = keepName };
        sim.Mergers.Add(Pea);
        var bot = OpenChest(new[] { Pea }, sim: sim);

        bot.TickUntil(() => bot.MovesOf(Pea) > 0, 1000);
        Check.Eq(bot.MovesOf(Pea), 1, "the pea's move is sent");
        long sent = _t;

        bot.TickUntil(() => FakeHost.Logs.Any(l => l.Contains("pickup confirmed for 0x80264001")), 6000);
        long confirmedAfter = _t - sent;
        Check.True(Log("pickup confirmed for 0x80264001").Contains("merged into a stack"), "confirmed as a merge: " + Log("pickup confirmed"));
        Check.True(confirmedAfter < 1200, $"confirmed at the server's answer (~0.8 s), not after PickupArriveMaxMs: {confirmedAfter} ms");

        // The chest is closed once the Return has played (~0.74 s more), not before.
        bot.TickUntil(() => FakeHost.Closes.Count > 0, 3000);
        long closedAfter = _t - sent;
        Check.Eq(FakeHost.Closes.Count, 1, "closed once");
        Check.True(closedAfter >= sim.PickupMs + sim.ReturnMs, $"not before the Return ended ({closedAfter} ms)");
        Check.True(closedAfter < 2000, $"and soon after it ({closedAfter} ms; it was ~4.8 s)");
    });

    // ── 2. Startup ────────────────────────────────────────────────────────────

    private static void DecidedItemBeforeIds() => WithClock(() =>
    {
        BaseWorld();
        const uint Gem = 0x80264101, Armor1 = 0x80264102, Armor2 = 0x80264103;
        AddItem(Gem, "Black Opal", ItemTypeGem);                                  // appraised: decided
        AddItem(Armor1, "Chainmail Tassets", ItemTypeArmor, appraised: false);    // stat rule: needs its ID
        AddItem(Armor2, "Leather Cap", ItemTypeArmor, appraised: false);
        FakeHost.Ints[(Armor1, KeyArmorLevel)] = 350;
        FakeHost.Ints[(Armor2, KeyArmorLevel)] = 120;
        var bot = OpenChest(new[] { Gem, Armor1, Armor2 }, armorStatRule: true);
        bot.Set("_busyCount", 3); // the client's busy count is up (the old assess-window gate held the pickup on it)

        // The contents settle (LootContentSettleMs' 50 ms floor), then the IDs go out.
        bot.TickUntil(() => FakeHost.IdRequests.Count > 0, 500);
        Check.True(FakeHost.IdRequests.Contains(Armor1) && FakeHost.IdRequests.Contains(Armor2), "the armor's IDs are requested");
        Check.Eq(bot.MovesOf(Gem), 1, "the gem is picked up on that same tick, while the armor IDs are out");
        Check.Eq(bot.MovesOf(Armor1) + bot.MovesOf(Armor2), 0, "the armor waits for its appraisal");

        // The gem lands; the armor still has no ID: still nothing taken, nothing closed.
        FakeHost.Containers[Gem] = Player;
        bot.TickFor(1500);
        Check.Eq(bot.MovesOf(Armor1) + bot.MovesOf(Armor2), 0, "no unappraised armor taken");
        Check.Eq(FakeHost.Closes.Count, 0, "the chest stays open while IDs are out");

        // The appraisals arrive: the good one is taken, the poor one left.
        FakeHost.Appraised.Add(Armor1);
        FakeHost.Appraised.Add(Armor2);
        bot.TickUntil(() => bot.MovesOf(Armor1) > 0, 1000);
        Check.Eq(bot.MovesOf(Armor1), 1, "the AL 350 armor is taken once appraised");
        FakeHost.Containers[Armor1] = Player;
        bot.TickUntil(() => FakeHost.Closes.Count > 0, 3000);
        Check.Eq(bot.MovesOf(Armor2), 0, "the AL 120 armor is left");
        Check.Eq(FakeHost.Closes.Count, 1, "then the chest closes");
    });

    // ── 3. Pipelining ─────────────────────────────────────────────────────────

    private static void NextMoveAtAnswer() => WithClock(() =>
    {
        BaseWorld();
        const uint G1 = 0x80264201, G2 = 0x80264202, G3 = 0x80264203;
        AddItem(G1, "Emerald", ItemTypeGem);
        AddItem(G2, "Ruby", ItemTypeGem);
        AddItem(G3, "Sapphire", ItemTypeGem);
        var bot = OpenChest(new[] { G1, G2, G3 });

        bot.TickUntil(() => bot.MovesOf(G1) > 0, 1000);
        Check.Eq(bot.MovesOf(G1), 1, "first move");

        // The server's pickup motion plays; the item is still in the chest.
        FakeHost.CastBusy = 1;
        bot.TickFor(700);
        Check.Eq(bot.MovesOf(G2), 0, "nothing else while the first item is still in the chest");

        // The answer: the item is in the pack and the Return starts (still animating).
        FakeHost.Containers[G1] = Player;
        bot.Tick();
        Check.Eq(bot.MovesOf(G2), 1, "the next move goes out on the tick the first item left the chest, during its Return");
        Check.True(FakeHost.Logs.Any(l => l.Contains("queuing 0x80264202")), "logged as queued behind the Return");

        // One queued at a time: G3 waits for G2's answer however long the animation runs.
        bot.TickFor(1400);
        Check.Eq(bot.MovesOf(G3), 0, "no second queued move");
        Check.Eq(bot.MovesOf(G2), 1, "and no early retry of the queued one while the character animates");

        FakeHost.Containers[G2] = Player;
        bot.Tick();
        Check.Eq(bot.MovesOf(G3), 1, "the third goes out at the second's answer");
        Check.Eq(FakeHost.Closes.Count, 0, "nothing closed yet");
    });

    private static void TooBusyRetried() => WithClock(() =>
    {
        BaseWorld();
        const uint G1 = 0x80264301, G2 = 0x80264302;
        AddItem(G1, "Emerald", ItemTypeGem);
        AddItem(G2, "Ruby", ItemTypeGem);
        var bot = OpenChest(new[] { G1, G2 });

        bot.TickUntil(() => bot.MovesOf(G1) > 0, 1000);
        FakeHost.CastBusy = 1;
        bot.TickFor(700);
        FakeHost.Containers[G1] = Player;
        bot.Tick();
        Check.Eq(bot.MovesOf(G2), 1, "G2 queued at G1's answer");

        // The server says it is too busy (the move beat the Return), then the save-failed for G2.
        FakeHost.WeenieError(0x1D);
        FakeHost.WeenieError(0, 0x00A0, G2);
        bot.TickFor(500);
        Check.Eq(bot.MovesOf(G2), 1, "no resend while the character still animates");

        FakeHost.CastBusy = 0;
        bot.Tick();
        Check.Eq(bot.MovesOf(G2), 2, "sent again on the first still tick (no retry window)");
        Check.True(FakeHost.Logs.Any(l => l.Contains("the server refused 0x80264302")), "the refusal is logged");
        Check.Eq(bot.Get<int>("_currentLootItemMoveAttempts"), 0, "the refused send didn't use up one of the 3 tries");

        // This time it goes through.
        FakeHost.CastBusy = 1;
        bot.TickFor(700);
        FakeHost.Containers[G2] = Player;
        bot.TickFor(700);
        FakeHost.CastBusy = 0;
        bot.TickUntil(() => FakeHost.Closes.Count > 0, 2000);
        Check.Eq(FakeHost.Closes.Count, 1, "both looted, then closed");
        Check.False(FakeHost.Logs.Any(l => l.Contains("pickup failed")), "nothing given up on");
    });

    // ── The close ─────────────────────────────────────────────────────────────

    private static void CloseWaitsForLastReturn() => WithClock(() =>
    {
        BaseWorld();
        const uint G1 = 0x80264401;
        AddItem(G1, "Emerald", ItemTypeGem);
        var bot = OpenChest(new[] { G1 });

        bot.TickUntil(() => bot.MovesOf(G1) > 0, 1000);
        FakeHost.CastBusy = 1;
        bot.TickFor(700);
        FakeHost.Containers[G1] = Player;   // answered: the Return plays
        bot.TickFor(700);
        Check.True(FakeHost.Logs.Any(l => l.Contains("pickup confirmed for 0x80264401")), "the pickup is confirmed at the answer");
        Check.Eq(FakeHost.Closes.Count, 0, "but the chest isn't closed while the Return plays");

        FakeHost.CastBusy = 0;
        bot.TickUntil(() => FakeHost.Closes.Count > 0, 1000);
        Check.Eq(FakeHost.Closes.Count, 1, "closed once the character stands still");
    });

    // ── The whole chest ───────────────────────────────────────────────────────

    private static void FullChestTiming() => WithClock(() =>
    {
        BaseWorld();
        var sim = new AceSim();
        var all = new List<uint>();
        uint id = 0x80265000;
        for (int i = 0; i < 14; i++) { AddItem(++id, $"Gem {i}", ItemTypeGem); all.Add(id); }
        for (int i = 0; i < 6; i++) { AddItem(++id, $"Pea {i}", ItemTypeMisc, stack: true); sim.Mergers.Add(id); all.Add(id); }
        for (int i = 0; i < 80; i++) { AddItem(++id, $"Junk {i}", ItemTypeArmor); all.Add(id); }
        // Keepers spread through the listing, as in a real cache.
        var rnd = new Random(7);
        var order = all.OrderBy(_ => rnd.Next()).ToList();
        var bot = OpenChest(order, sim: sim);
        Check.Eq(bot.Cache.GetContainedItems(unchecked((int)Chest)).Count(), 100, "all 100 items listed in the chest");
        long opened = _t;

        bot.TickUntil(() => FakeHost.Closes.Count > 0, 120_000);
        long total = _t - opened;
        int keepersTaken = FakeHost.ExternalMoves.Select(m => m.Id).Distinct().Count();
        long serverFloor = 20 * (sim.PickupMs + sim.ReturnMs);
        Console.WriteLine($"          (100 items, 20 keepers: {total / 1000.0:0.0} s open to close; ACE's own animations {serverFloor / 1000.0:0.0} s; " +
                          $"queued {sim.Queued}, too busy {sim.TooBusy}, refused by the client {sim.LocalRefused})");
        Check.Eq(FakeHost.Closes.Count, 1, "the chest is closed at the end");
        Check.Eq(keepersTaken, 20, "all 20 keepers taken, no junk");
        Check.Eq(sim.Answers.Count, 20, "20 answers from the server");
        Check.Eq(sim.TooBusy, 0, "no 'You're too busy'");
        Check.Eq(sim.LocalRefused, 0, "no move refused by the client's one-at-a-time slot");
        Check.True(sim.Queued >= 18, $"the moves were queued behind the Returns ({sim.Queued})");
        Check.True(total < serverFloor + 1500, $"within 1.5 s of ACE's own animation time: {total} ms vs {serverFloor} ms (was ~50 s)");
        Check.False(FakeHost.Logs.Any(l => l.Contains("pickup failed")), "nothing given up on");
    });

    // ── 4. Log spam ───────────────────────────────────────────────────────────

    private static void LootEvalLoggedOnce() => WithClock(() =>
    {
        BaseWorld();
        var sim = new AceSim();
        var ids = new List<uint>();
        uint id = 0x80266000;
        for (int i = 0; i < 5; i++) { AddItem(++id, $"Gem {i}", ItemTypeGem); ids.Add(id); }
        var bot = OpenChest(ids, sim: sim);
        bot.TickUntil(() => FakeHost.Closes.Count > 0, 30_000);
        Check.Eq(FakeHost.Closes.Count, 1, "looted and closed");
        foreach (uint g in ids)
        {
            int lines = FakeHost.Logs.Count(l => l.Contains("[LootEval]") && l.Contains($"id=0x{g:X8}"));
            Check.Eq(lines, 1, $"one [LootEval] line for 0x{g:X8}");
        }
    });
}

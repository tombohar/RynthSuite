using System;
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

// /ub give and /mt give (Tom, 2026-10-05): "/ub give ... should give every item that matches the
// name in the inventory. /mt give should give the first one it finds." Both use AC's give action
// (an NPC ignores MoveItemExternal) and never hand over worn or wielded gear.
internal static class GiveTests
{
    private const uint Player = 0x50000F01;
    private const uint Npc = 0x80281000;
    private const uint Mote1 = 0x80281101, Mote2 = 0x80281102, Mote3 = 0x80281103, WornMote = 0x80281104, Other = 0x80281105;
    private const BindingFlags Any = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

    public static void Register(Runner r)
    {
        r.Add("give: /ub give hands over every matching carried item, never worn gear", UbGivesAll);
        r.Add("give: /ub give with a count hands over that many", UbGiveCount);
        r.Add("give: /mt give hands over only the first match", MtGivesFirst);
        r.Add("give: /ub givep matches part of the name", UbGivePartial);
        r.Add("give: a meta's /ub give hands over every match too (it used to take /mt give's one)", MetaUbGivesAll);
        r.Add("give: give-all never hands over RynthAi's own items, attuned or equipped ones", GiveAllSkipsProtected);
        r.Add("give: a running meta's give-all also keeps what the loot profile keeps; a typed one does not", GiveAllLootKeepOnlyForMeta);
        r.Add("give: give-all logs every stack it queues and caps one command at 500", GiveAllLogsAndCaps);
        r.Add("give: a delayed /ub give keeps the standing of the line that queued it", DelayedGiveKeepsStanding);
        r.Add("give: the next give waits until the server took the last (it answers extra gives 'too busy')", WaitsForServer);
        r.Add("give: a whole stack to an NPC that takes one per give keeps giving until it is gone, one give per tick", NpcStackOneAtATime);
        r.Add("give: the answer and the next give share one tick (the interval is only a minimum gap between sends)", NextGiveSameTick);
        r.Add("give: busy refusals re-send with no cap", BusyResendsUncapped);
        r.Add("give: a permanent refusal skips the stack at once and moves on", PermanentSkips);
        r.Add("give: item not found after a re-send counts as given", ItemNotFoundAfterResendCounts);
        r.Add("give: the summary counts items, not stacks", SummaryCountsItems);
        r.Add("give: no answer at all re-sends once, then skips", NoAnswerSkips);
        r.Add("give: an unreadable stack count is no information, not a shrink", UnreadableStackIsNotAShrink);
        r.Add("give: an item unreadable for one tick is not gone; for two it is", OneTickUnreadableIsNotGone);
        r.Add("give: speech lines never answer a give; item not found with the stack unchanged is a skip", SpeechIgnoredAndNotFoundNeedsGone);
        r.Add("give: busy notice after 10 s, busy logging throttled, unclassified lines logged (max 10)", BusyNoticeAndLogThrottle);
        r.Add("give: a counted give from one stack goes through the queue (NPC one per give, player split once)", CountedSingleStack);
        r.Add("give: cancel in the middle of a busy wait stops everything", CancelMidBusy);
        r.Add("give: an accept line does not shorten the two-tick rule for an unreadable item", AcceptLineDoesNotAbandon);
        r.Add("give: a failed stack read on a stackable item sends nothing until it reads", FailedStackReadOnStackableWaits);
        r.Add("give: a container we cannot place is unreadable, not gone", UnplaceableContainerIsUnreadable);
        r.Add("give: a stack count that never reads holds the queue for 2 s, then the give goes out and the queue moves on", UnreadableStackFallsBackAfterTwoSeconds);
        r.Add("give: an unreadable stack never turns a counted give into a whole-object give; only give-all goes out whole", UnreadableStackNeverGivesWholeForACount);
        r.Add("give: a count-1 stackable whose count property is absent still gets through", CountOneStackableWithoutCountGetsThrough);
        r.Add("give: /ub givea queues a give-all (0), so an unreadable count there sends the whole object", GiveAllQueuesWholeStack);
        r.Add("give: a target that stays busy for a minute is skipped and counted", BusyGivesUpAfterAMinute);
        r.Add("give: a no-answer re-send that could not go out still counts as a try", FailedResendCountsAsATry);
    }

    private sealed class World
    {
        public readonly RynthAiPlugin P = new();
        public readonly LegacyUiSettings S = new() { GiveQueueIntervalMs = 0 };
        /// <summary>The queue's fake millisecond clock.</summary>
        public long Now;
        public readonly WorldObjectCache Cache;

        public World()
        {
            FakeHost.Reset();
            FakeHost.SalvageCalls = true;   // MoveItemExternal + GetObjectWielderInfo (Wielded)
            FakeHost.GiveCalls = true;
            FakeHost.Names[Player] = "Giver";
            FakeHost.PlayerIdValue = Player;
            FakeHost.Names[Npc] = "Town Crier";
            FakeHost.Positions[Npc] = (0x1048002Au, 101f, 100f, 0f);
            FakeHost.ItemTypes[Npc] = 0x10;   // a creature (an NPC)
            FakeHost.NotAttackable.Add(Npc);
            FakeHost.Positions[Player] = (0x1048002Au, 100f, 100f, 0f);
            FakeHost.PlayerPose = (0x1048002Au, 100f, 100f, 0f);
            foreach (var (id, name) in new[] { (Mote1, "Mana Mote"), (Mote2, "Mana Mote"), (Mote3, "Mana Mote"), (WornMote, "Mana Mote"), (Other, "Lesser Mana Mote") })
            {
                FakeHost.Names[id] = name;
                FakeHost.Containers[id] = Player;
                FakeHost.ItemTypes[id] = 0x80;   // misc
            }
            FakeHost.Wielded[WornMote] = 0x8000;   // worn: never given
            var host = FakeHost.Create(playerId: Player);
            typeof(RynthPluginBase).GetMethod("Attach", Any)!.Invoke(P, new object[] { FakeHost.LastApi });
            var dash = (LegacyDashboardRenderer)RuntimeHelpers.GetUninitializedObject(typeof(LegacyDashboardRenderer));
            typeof(LegacyDashboardRenderer).GetField("_settings", Any)!.SetValue(dash, S);
            Set("_dashboard", dash);
            Cache = new WorldObjectCache(host);
            Cache.SetPlayerId(Player);
            Set("_objectCache", Cache);
            Set("_playerId", Player);
            Set("_initialized", true);
            Set("_loginComplete", true);
            Set("_giveClock", (Func<long>)(() => Now));
            foreach (uint id in new[] { Npc, Mote1, Mote2, Mote3, WornMote, Other }) Cache.OnCreateObject(id);
            Cache.Tick();
        }

        public void Set(string f, object? v) => typeof(RynthAiPlugin).GetField(f, Any)!.SetValue(P, v);
        public object? Call(string m, params object?[] a) => typeof(RynthAiPlugin).GetMethod(m, Any)!.Invoke(P, a);
        public int LogWith(string text) => FakeHost.Logs.Count(l => l.Contains(text, StringComparison.Ordinal));
        public int ChatWith(string text) => FakeHost.Chat.Count(c => c.Contains(text, StringComparison.Ordinal));

        /// <summary>Runs the chat command, then drains the give queue.</summary>
        public void Run(string command)
        {
            if (command.StartsWith("/ub", StringComparison.Ordinal)) Call("HandleUbCommand", command);
            else Call("HandleMtCommand", command);
            for (int i = 0; i < 20; i++) Call("DrainGiveQueue");
        }

        /// <summary>Advances the clock, then runs one tick of the give queue.</summary>
        public void Tick(long advanceMs = 0) { Now += advanceMs; Call("DrainGiveQueue"); }

        public void Chat(string text, int chatType = 0) => Call("NoteGiveChat", text, chatType);

        /// <summary>Adds a carried stack the cache knows about.</summary>
        public void AddStack(uint id, string name, int count)
        {
            FakeHost.Names[id] = name;
            FakeHost.Containers[id] = Player;
            FakeHost.ItemTypes[id] = 0x80;
            FakeHost.Ints[(id, 12)] = count;   // StackCount
            Cache.OnCreateObject(id);
            Cache.Tick();
        }

        /// <summary>An NPC with a Give emote: each give takes ONE item from the stack (ACE: acceptAll ? amount : 1).</summary>
        public void NpcTakesOnePerGive()
        {
            FakeHost.GiveHandler = (item, target, amount) =>
            {
                if (!FakeHost.Ints.TryGetValue((item, 12), out int n)) { FakeHost.Containers[item] = target; return; }   // a single item goes whole
                if (n <= 0) return;
                FakeHost.Ints[(item, 12)] = n - 1;
                if (n - 1 <= 0) FakeHost.Containers[item] = target;
            };
        }

        public bool ChatHas(string part) => FakeHost.Chat.Any(c => c.Contains(part, StringComparison.Ordinal));

        public uint[] GivenItems => FakeHost.Gives.Select(g => g.Item).ToArray();
    }

    private static void UbGivesAll()
    {
        var w = new World();
        w.Run("/ub give Mana Mote to Town Crier");
        Check.Eq(FakeHost.Gives.Count, 3, "all three carried Mana Motes");
        Check.True(new[] { Mote1, Mote2, Mote3 }.All(id => w.GivenItems.Contains(id)), "each of them");
        Check.False(w.GivenItems.Contains(WornMote), "the worn one stays on");
        Check.False(w.GivenItems.Contains(Other), "an exact name doesn't take 'Lesser Mana Mote'");
        Check.True(FakeHost.Gives.All(g => g.Target == Npc), "all to the NPC, with AC's give action");
        Check.Eq(FakeHost.ExternalMoves.Count, 0, "never MoveItemExternal (an NPC ignores it)");
    }

    private static void UbGiveCount()
    {
        var w = new World();
        w.Run("/ub give 2 Mana Mote to Town Crier");
        Check.Eq(FakeHost.Gives.Count, 2, "a count gives that many");
    }

    private static void MtGivesFirst()
    {
        var w = new World();
        w.Run("/mt give Mana Mote to Town Crier");
        Check.Eq(FakeHost.Gives.Count, 1, "/mt give: one item");
        Check.False(w.GivenItems.Contains(WornMote), "and never the worn one");
    }

    private static void MetaUbGivesAll()
    {
        var w = new World();
        w.S.IsMacroRunning = true;
        w.S.EnableMeta = true;
        w.Call("HandleMetaChatCommand", "/ub give Mana Mote to Town Crier");
        for (int i = 0; i < 20; i++) w.Call("DrainGiveQueue");
        Check.Eq(FakeHost.Gives.Count, 3, "all three carried Mana Motes, as typed");
        Check.False(FakeHost.Gives.Any(g => g.Item == WornMote), "and never the worn one");
    }

    private static void GiveAllSkipsProtected()
    {
        var w = new World();
        // Mote2 is on the Weapons list, Mote3 on the Consumables list: RynthAi is using them.
        w.S.ItemRules.Add(new ItemRule { Id = unchecked((int)Mote2), Name = "Mana Mote" });
        w.S.ConsumableRules.Add(new ConsumableRule { Id = unchecked((int)Mote3), Name = "Mana Mote" });
        w.Run("/ub give Mana Mote to Town Crier");
        Check.Eq(FakeHost.Gives.Count, 1, "only the unprotected mote goes");
        Check.Eq(FakeHost.Gives[0].Item, Mote1, "that one");
        Check.Eq(w.LogWith("skipped 0x80281102 'Mana Mote': on RynthAi's Weapons list"), 1, "the Weapons-list skip is logged");
        Check.Eq(w.LogWith("skipped 0x80281103 'Mana Mote': on RynthAi's Consumables list"), 1, "and the Consumables-list one");
        Check.True(w.ChatWith("2 protected stack(s) skipped") == 1, "and the summary counts them");

        // A monster rule's weapon is protected too.
        var w2 = new World();
        w2.S.MonsterRules.Add(new MonsterRule { Name = "Drudge", WeaponId = unchecked((int)Mote1), OffhandId = unchecked((int)Mote2) });
        w2.Run("/ub give Mana Mote to Town Crier");
        Check.True(FakeHost.Gives.Count == 1 && FakeHost.Gives[0].Item == Mote3, "a monster rule's weapon and off hand stay");

        // An attuned item can't be given, and isn't tried.
        var w3 = new World();
        foreach (uint id in new[] { Mote1, Mote2, Mote3 }) FakeHost.Ints[(id, 114u)] = 1;
        w3.Run("/ub give Mana Mote to Town Crier");
        Check.Eq(FakeHost.Gives.Count, 0, "attuned motes are not given");
        Check.True(w3.ChatWith("Nothing to give") == 1, "and it says why nothing was queued");
    }

    private static string KeepEverythingProfile()
    {
        string path = Path.Combine(Program.TempRoot, "give-keep-all.json");
        var p = new LootProfile { Name = "keep all" };
        p.Rules.Add(new LootRule { Name = "All", Action = LootAction.Keep });
        p.Save(path);
        return path;
    }

    private static void GiveAllLootKeepOnlyForMeta()
    {
        // Typed: the user asked for it; only the always-on protections apply.
        var typed = new World();
        typed.S.CurrentLootPath = KeepEverythingProfile();
        typed.Run("/ub give Mana Mote to Town Crier");
        Check.Eq(FakeHost.Gives.Count, 3, "typed /ub give hands over what the loot profile keeps");

        // From a running meta: what the loot profile keeps stays.
        var meta = new World();
        meta.S.CurrentLootPath = KeepEverythingProfile();
        meta.S.IsMacroRunning = true;
        meta.S.EnableMeta = true;
        meta.Call("HandleMetaChatCommand", "/ub give Mana Mote to Town Crier");
        for (int i = 0; i < 20; i++) meta.Call("DrainGiveQueue");
        Check.Eq(FakeHost.Gives.Count, 0, "a meta's /ub give leaves what the loot profile keeps");
        Check.Eq(meta.LogWith("kept by the loot profile"), 3, "each skip is logged");
        Check.True(meta.ChatWith("Nothing to give") == 1, "and said");

        // The same meta with the macro stopped is not a running meta: the typed rules apply.
        var stopped = new World();
        stopped.S.CurrentLootPath = KeepEverythingProfile();
        stopped.S.IsMacroRunning = false;
        stopped.Call("HandleMetaChatCommand", "/ub give Mana Mote to Town Crier");
        for (int i = 0; i < 20; i++) stopped.Call("DrainGiveQueue");
        Check.Eq(FakeHost.Gives.Count, 3, "without a running meta it is a plain give");
    }

    private static void GiveAllLogsAndCaps()
    {
        var w = new World();
        // 520 carried pebbles: more than one command may queue.
        const int Pebbles = 520;
        uint first = 0x80290000;
        for (int i = 0; i < Pebbles; i++)
        {
            uint id = first + (uint)i;
            FakeHost.Names[id] = "Pebble";
            FakeHost.Containers[id] = Player;
            FakeHost.ItemTypes[id] = 0x80;
        }
        for (int i = 0; i < Pebbles; i++) w.Cache.OnCreateObject(first + (uint)i);
        for (int i = 0; i < 6; i++) w.Cache.Tick();   // the cache classifies 250 objects a tick
        w.Call("HandleUbCommand", "/ub give Pebble to Town Crier");
        var queue = (System.Collections.ICollection)typeof(RynthAiPlugin).GetField("_pendingGives", Any)!.GetValue(w.P)!;
        Check.Eq(queue.Count, RynthAiPlugin.MaxGiveAllPerCommand, "one command queues at most 500");
        Check.Eq(w.LogWith("[Give] queued 0x"), RynthAiPlugin.MaxGiveAllPerCommand, "one log line per stack queued");
        Check.Eq(w.LogWith("the cap is 500"), 1, "the cap is logged");
        Check.True(w.ChatWith("capped at 500") == 1, "and said in chat");

        // Below the cap nothing is cut.
        var small = new World();
        small.Call("HandleUbCommand", "/ub give Mana Mote to Town Crier");
        var q2 = (System.Collections.ICollection)typeof(RynthAiPlugin).GetField("_pendingGives", Any)!.GetValue(small.P)!;
        Check.Eq(q2.Count, 3, "three motes, three queued");
        Check.Eq(small.LogWith("the cap is"), 0, "no cap line");
    }

    private static void DelayedGiveKeepsStanding()
    {
        // A running meta that queues "/ub delay 0 /ub give ..." gives as a running meta when it fires,
        // so the loot profile's Keep rules still protect; a typed delay does not.
        var meta = new World();
        meta.S.CurrentLootPath = KeepEverythingProfile();
        meta.S.IsMacroRunning = true;
        meta.S.EnableMeta = true;
        meta.Call("HandleMetaChatCommand", "/ub delay 0 /ub give Mana Mote to Town Crier");
        meta.Call("TickDelayedCommands");
        for (int i = 0; i < 20; i++) meta.Call("DrainGiveQueue");
        Check.Eq(FakeHost.Gives.Count, 0, "the delayed line still counts as the meta's");

        var typed = new World();
        typed.S.CurrentLootPath = KeepEverythingProfile();
        typed.S.IsMacroRunning = true;
        typed.S.EnableMeta = true;
        typed.Call("HandleUbCommand", "/ub delay 0 /ub give Mana Mote to Town Crier");
        typed.Call("TickDelayedCommands");
        for (int i = 0; i < 20; i++) typed.Call("DrainGiveQueue");
        Check.Eq(FakeHost.Gives.Count, 3, "a typed delay is a typed give");
    }

    private static void UbGivePartial()
    {
        var w = new World();
        w.Run("/ub givep Mana Mote to Town");
        Check.Eq(FakeHost.Gives.Count, 4, "partial: the three Mana Motes and the Lesser Mana Mote");
        Check.False(w.GivenItems.Contains(WornMote), "still not the worn one");
    }

    // 2026-10-08: the queue sent a give every 150 ms; the server takes one at a time and the
    // rest came back "too busy", so /ub give handed over one item.
    private static void WaitsForServer()
    {
        var w = new World();
        FakeHost.GiveLands = false;            // the server hasn't taken the first yet
        w.Run("/ub give Mana Mote to Town Crier");
        Check.Eq(FakeHost.Gives.Count, 1, "one give, then it waits for the server");

        FakeHost.Containers[FakeHost.Gives[0].Item] = Npc;   // the server took it
        FakeHost.GiveLands = true;
        for (int i = 0; i < 20; i++) w.Call("DrainGiveQueue");
        Check.Eq(FakeHost.Gives.Count, 3, "then the other two, each after the last landed");
        Check.Eq(FakeHost.Gives.Select(g => g.Item).Distinct().Count(), 3, "no item sent twice");
    }

    private const uint Carapace = 0x80281301;

    // 2026-10-12: Olthoi Carapace stack of 100 to a Collector NPC: the server takes ONE per give, the
    // stack stays in the pack, and the queue used to wait 3 s, re-send once and skip.
    private static void NpcStackOneAtATime()
    {
        var w = new World();
        w.AddStack(Carapace, "Olthoi Slayer Carapace", 5);
        w.NpcTakesOnePerGive();
        w.Call("EnqueueGive", Carapace, Npc, 5);
        w.Tick();
        Check.Eq(FakeHost.Gives.Count, 1, "the first give goes out");
        for (int expected = 2; expected <= 5; expected++)
        {
            w.Tick();   // the stack shrank: the same item goes again, in this very tick
            Check.Eq(FakeHost.Gives.Count, expected, $"give #{expected} sent on the tick that saw the shrink");
        }
        Check.True(FakeHost.Gives.All(g => g.Item == Carapace && g.Target == Npc), "always the same stack, to the NPC");
        w.Tick();
        Check.Eq(FakeHost.Gives.Count, 5, "the stack is gone: nothing more is sent");
        Check.False(w.ChatHas("didn't take"), "never reported as not taken");
    }

    private static void NextGiveSameTick()
    {
        var w = new World();
        w.S.GiveQueueIntervalMs = 150;   // a saved old default: only a minimum gap between SENDS
        w.Run("/ub give Mana Mote to Town Crier");
        Check.Eq(FakeHost.Gives.Count, 1, "one give out, the server not yet heard from at t=0");
        // The first landed at once in the fake. Less than the gap since the send: the next must wait.
        w.Tick(100);
        Check.Eq(FakeHost.Gives.Count, 1, "inside the minimum gap the next send waits");
        // A round trip longer than the gap: answer observed and the next give leaves in the same tick.
        w.Tick(60);
        Check.Eq(FakeHost.Gives.Count, 2, "answer seen and the next give sent on one tick");
        w.Tick(200);
        Check.Eq(FakeHost.Gives.Count, 3, "and again for the third");
    }

    private static void BusyResendsUncapped()
    {
        var w = new World();
        FakeHost.GiveLands = false;
        w.Call("EnqueueGive", Mote1, Npc, 1);
        w.Tick();
        for (int busy = 1; busy <= 6; busy++)
        {
            int before = FakeHost.Gives.Count;
            w.Chat("You're too busy!");
            w.Tick(100);
            Check.Eq(FakeHost.Gives.Count, before, $"busy #{busy}: not re-sent before the short delay");
            w.Tick(300);
            Check.Eq(FakeHost.Gives.Count, before + 1, $"busy #{busy}: re-sent after it");
        }
        Check.False(w.ChatHas("didn't take"), "no cap: never skipped");
        FakeHost.GiveLands = true;
        FakeHost.Containers[Mote1] = Npc;   // finally taken
        w.Tick(100);
        Check.True(w.ChatHas("1 item(s) given"), "then it lands and counts");
        Check.Eq(FakeHost.Gives.Count, 7, "seven sends: the first plus six re-sends");
    }

    private static void PermanentSkips()
    {
        var w = new World();
        FakeHost.GiveLands = false;
        w.Call("EnqueueGive", Mote1, Npc, 1);
        w.Call("EnqueueGive", Mote2, Npc, 1);
        w.Tick();
        Check.Eq(FakeHost.Gives.Count, 1, "first stack out");
        w.Chat("Town Crier is not accepting gifts right now.");
        w.Tick();
        Check.Eq(FakeHost.Gives.Count, 2, "refused for good: the next stack is sent on that same tick");
        Check.Eq(FakeHost.Gives[1].Item, Mote2, "the second stack, not a retry of the first");
        FakeHost.Containers[Mote2] = Npc;
        w.Tick();
        Check.True(w.ChatHas("1 item(s) given, 1 stack(s) skipped"), "summary: one given, one skipped with its reason");
    }

    private static void ItemNotFoundAfterResendCounts()
    {
        var w = new World();
        FakeHost.GiveLands = false;
        w.Call("EnqueueGive", Mote1, Npc, 1);
        w.Tick();
        w.Tick(3001);   // no answer: sent once more
        Check.Eq(FakeHost.Gives.Count, 2, "re-sent after the confirm time");
        FakeHost.Containers[Mote1] = Npc;   // the first send did land: the item is gone from the pack
        w.Chat("Item not found!");
        w.Tick();
        Check.Eq(FakeHost.Gives.Count, 2, "no third send");
        Check.True(w.ChatHas("1 item(s) given, 0 stack(s) skipped"), "the earlier send landed: counted as given, not skipped");
    }

    private static void SummaryCountsItems()
    {
        var w = new World();
        w.AddStack(Carapace, "Olthoi Slayer Carapace", 5);
        w.NpcTakesOnePerGive();
        w.Call("EnqueueGive", Carapace, Npc, 5);
        w.Call("EnqueueGive", Mote1, Npc, 1);
        for (int i = 0; i < 12; i++) w.Tick(50);
        Check.True(w.ChatHas("Give queue complete: 6 item(s) given, 0 stack(s) skipped"), "5 carapaces  + 1 mote = 6 items from 2 stacks");
    }

    private static void NoAnswerSkips()
    {
        var w = new World();
        FakeHost.GiveLands = false;
        w.Call("EnqueueGive", Mote1, Npc, 1);
        w.Tick();
        w.Tick(3001);
        w.Tick(3001);
        Check.Eq(FakeHost.Gives.Count, 2, "two tries, then it gives up");
        Check.True(w.ChatHas("didn't take"), "and says so");
        Check.True(w.ChatHas("1 stack(s) skipped"), "the summary counts it as skipped");
    }

    private static void UnreadableStackIsNotAShrink()
    {
        var w = new World();
        FakeHost.GiveHandler = (_, _, _) => { };   // the server has not answered yet
        w.AddStack(Carapace, "Olthoi Slayer Carapace", 5);
        w.Call("EnqueueGive", Carapace, Npc, 5);
        w.Tick();
        Check.Eq(FakeHost.Gives.Count, 1, "sent");
        FakeHost.Ints.Remove((Carapace, 12));   // the engine read fails for a tick
        w.Tick();
        Check.Eq(FakeHost.Gives.Count, 1, "a failed read is not a shrink of 4: no second give while the first is in flight");
        FakeHost.Ints[(Carapace, 12)] = 5;
        w.Tick();
        Check.Eq(FakeHost.Gives.Count, 1, "read again, unchanged: still waiting");
        Check.False(w.ChatHas("item(s) given"), "nothing credited");
        FakeHost.Ints[(Carapace, 12)] = 4;       // now a real shrink
        w.Tick();
        Check.Eq(FakeHost.Gives.Count, 2, "a real shrink gives again");
    }

    private static void OneTickUnreadableIsNotGone()
    {
        var w = new World();
        FakeHost.GiveHandler = (_, _, _) => { };
        w.Call("EnqueueGive", Mote1, Npc, 1);
        w.Call("EnqueueGive", Mote2, Npc, 1);
        w.Tick();
        FakeHost.Containers[Mote1] = 0;   // one unreadable tick
        w.Tick();
        Check.Eq(FakeHost.Gives.Count, 1, "Container 0 for one tick is not gone");
        FakeHost.Containers[Mote1] = Player;
        w.Tick();
        FakeHost.Containers[Mote1] = 0;
        w.Tick();
        Check.Eq(FakeHost.Gives.Count, 1, "the streak restarted after a good read");
        w.Tick();
        Check.Eq(FakeHost.Gives.Count, 2, "unreadable on two ticks in a row: gone, the next stack goes");
    }

    private static void SpeechIgnoredAndNotFoundNeedsGone()
    {
        var w = new World();
        FakeHost.GiveHandler = (_, _, _) => { };
        w.Call("EnqueueGive", Mote1, Npc, 1);
        w.Call("EnqueueGive", Mote2, Npc, 1);
        w.Tick();
        w.Chat("<Tell:IIDString:1:Bob>Bob<\\Tell> says, \"Town Crier doesn't want that\"", 17);
        w.Chat("Bob says, \"Town Crier doesn't want that\"", 0);   // shape alone is enough
        w.Chat("Bob tells you, \"not accepting gifts\"", 3);
        w.Tick();
        Check.Eq(FakeHost.Gives.Count, 1, "other players' words are no answer: still in flight, nothing skipped");
        Check.False(w.ChatHas("skipped"), "no summary yet");
        w.Tick(3001);   // re-sent
        w.Chat("Item not found!");
        w.Tick();       // the item is still carried, stack unchanged: a skip, not a delivery
        Check.Eq(FakeHost.Gives.Count, 3, "item not found with the item still in the pack skips it; the next stack goes");
        FakeHost.Containers[Mote2] = Npc;
        w.Tick();
        Check.True(w.ChatHas("1 item(s) given, 1 stack(s) skipped"), "one skipped as item not found, one given");
    }

    private static void BusyNoticeAndLogThrottle()
    {
        var w = new World();
        FakeHost.GiveHandler = (_, _, _) => { };
        w.Call("EnqueueGive", Mote1, Npc, 1);
        w.Tick();
        w.Chat("You cast Inner Calm on yourself", 7);   // magic lines never take a slot
        for (int i = 0; i < 12; i++) w.Chat("Something odd " + i);
        Check.Eq(FakeHost.Logs.Count(l => l.Contains("[Give] unclassified chat:")), 10, "at most 10 unclassified lines logged per give");
        int busyCycles = 0;
        long start = w.Now;
        while (w.Now - start < 9000)
        {
            w.Chat("You're too busy!");
            w.Tick(0);
            w.Tick(300);
            busyCycles++;
        }
        Check.False(w.ChatHas("still retrying"), "no notice before 10 s of busy waiting");
        while (w.Now - start < 10700)
        {
            w.Chat("You're too busy!");
            w.Tick(0);
            w.Tick(300);
            busyCycles++;
        }
        Check.Eq(FakeHost.Chat.Count(c => c.Contains("Town Crier is busy - still retrying (/ra givea stop to cancel).")), 1, "one notice at about 10 s");
        int busyLogs = FakeHost.Logs.Count(l => l.Contains("Give answer: busy"));
        Check.True(busyCycles >= 30 && busyLogs <= busyCycles / 10 + 1, $"busy logging throttled: {busyLogs} lines for {busyCycles} busy answers");
        Check.True(busyLogs >= 1, "the first busy is logged");
    }

    private static void CountedSingleStack()
    {
        var w = new World();
        w.AddStack(Carapace, "Olthoi Slayer Carapace", 5);
        w.NpcTakesOnePerGive();
        w.Run("/ub give 3 Olthoi Slayer Carapace to Town Crier");
        Check.Eq(FakeHost.Gives.Count, 3, "3 of 5 to a one-per-give NPC: three gives");
        Check.Eq(FakeHost.Ints[(Carapace, 12)], 2, "two left in the pack");
        Check.Eq(FakeHost.Containers[Carapace], Player, "the stack stays");

        var w2 = new World();
        const uint Friend = 0x50000F02;
        FakeHost.Names[Friend] = "Friend";
        FakeHost.Positions[Friend] = (0x1048002Au, 101f, 101f, 0f);
        FakeHost.ItemTypes[Friend] = 0x10;
        w2.Cache.OnCreateObject(Friend);
        w2.Cache.Tick();
        w2.AddStack(Carapace, "Olthoi Slayer Carapace", 5);
        FakeHost.GiveHandler = (item, target, amount) =>
        {
            FakeHost.Ints[(item, 12)] -= amount == 0 ? FakeHost.Ints[(item, 12)] : amount;   // a player takes what is asked
        };
        w2.Run("/ub give 3 Olthoi Slayer Carapace to Friend");
        Check.Eq(FakeHost.Gives.Count, 1, "to a player: one give");
        Check.Eq(FakeHost.Gives[0].Amount, 3, "carrying the count (the server splits)");
        Check.Eq(FakeHost.Ints[(Carapace, 12)], 2, "two left");
    }

    private static void CancelMidBusy()
    {
        var w = new World();
        FakeHost.GiveHandler = (_, _, _) => { };
        w.Call("EnqueueGive", Mote1, Npc, 1);
        w.Call("EnqueueGive", Mote2, Npc, 1);
        w.Tick();
        w.Chat("You're too busy!");
        w.Tick(100);   // busy seen, re-send armed
        w.Call("CancelGiveQueue");
        for (int i = 0; i < 20; i++) w.Tick(500);
        Check.Eq(FakeHost.Gives.Count, 1, "nothing is sent after the cancel: no re-send, no next stack");
        Check.True(w.ChatHas("Give queue cancelled"), "cancel reported");
    }

    private static void AcceptLineDoesNotAbandon()
    {
        var w = new World();
        FakeHost.GiveHandler = (_, _, _) => { };
        w.Call("EnqueueGive", Mote1, Npc, 1);
        w.Call("EnqueueGive", Mote2, Npc, 1);
        w.Tick();
        w.Chat("You give Town Crier Mana Mote.");
        FakeHost.Containers[Mote1] = 0;   // one unreadable tick
        w.Tick();
        Check.Eq(FakeHost.Gives.Count, 1, "an accept line plus one unreadable tick does not abandon the stack");
        FakeHost.Containers[Mote1] = Player;
        w.Tick();
        Check.Eq(FakeHost.Gives.Count, 1, "read again, still carried");
    }

    private static void FailedStackReadOnStackableWaits()
    {
        var w = new World();
        FakeHost.GiveHandler = (_, _, _) => { };
        w.AddStack(Carapace, "Olthoi Slayer Carapace", 5);
        FakeHost.Ints[(Carapace, (uint)(int)LongValueKey.MaxStackSize)] = 100;
        FakeHost.Ints.Remove((Carapace, 12));   // the stack read fails
        w.Call("EnqueueGive", Carapace, Npc, 1);
        w.Tick();
        w.Tick();
        Check.Eq(FakeHost.Gives.Count, 0, "a stackable item with an unreadable stack is not sent (amount 1 would hand over the whole stack)");
        FakeHost.Ints[(Carapace, 12)] = 5;
        w.Tick();
        Check.Eq(FakeHost.Gives.Count, 1, "sent on the first tick that reads it");
        Check.Eq(FakeHost.Gives[0].Amount, 1, "one item, not the whole object");
    }

    private static void UnplaceableContainerIsUnreadable()
    {
        const uint Pack = 0x80281F01, Stranger = 0x80281F02;
        var w = new World();
        FakeHost.GiveHandler = (_, _, _) => { };
        w.Call("EnqueueGive", Mote1, Npc, 1);
        w.Call("EnqueueGive", Mote2, Npc, 1);
        w.Tick();
        // In a side pack that itself reads as inside the player's pack: carried.
        FakeHost.Containers[Pack] = Player;
        FakeHost.Containers[Mote1] = Pack;
        w.Tick(); w.Tick(); w.Tick();
        Check.Eq(FakeHost.Gives.Count, 1, "a pack the player carries: still carried");
        // In a container whose own container reads 0: unreadable, needs two ticks.
        FakeHost.Containers.Remove(Stranger);
        FakeHost.Containers[Mote1] = Stranger;
        w.Tick();
        Check.Eq(FakeHost.Gives.Count, 1, "one tick of an unplaceable container is not gone");
        FakeHost.Containers[Mote1] = Pack;
        w.Tick();
        FakeHost.Containers[Mote1] = Stranger;
        w.Tick();
        Check.Eq(FakeHost.Gives.Count, 1, "the streak restarts after a good read");
        w.Tick();
        Check.Eq(FakeHost.Gives.Count, 2, "two unreadable ticks in a row: gone");
    }

    // Review of PR 18: a stackable item whose StackCount read stayed absent made SendGive return false on
    // every tick, for ever, with nothing logged and nothing behind it in the queue ever sent.
    private static void UnreadableStackFallsBackAfterTwoSeconds()
    {
        var w = new World();
        w.Now = 1000;
        w.AddStack(Carapace, "Olthoi Slayer Carapace", 5);
        FakeHost.Ints[(Carapace, (uint)(int)LongValueKey.MaxStackSize)] = 100;
        FakeHost.Ints.Remove((Carapace, 12));   // the stack read never succeeds
        FakeHost.GiveHandler = (item, target, _) => FakeHost.Containers[item] = target;   // the server takes it
        w.Call("EnqueueGive", Carapace, Npc, 0);   // a genuine give-all
        w.Call("EnqueueGive", Mote1, Npc, 1);
        w.Tick();
        w.Tick(1500);
        Check.Eq(FakeHost.Gives.Count, 0, "inside the 2 s the unreadable stack still waits");
        w.Tick(600);   // 2.1 s after the first failed read
        Check.Eq(FakeHost.Gives.Count, 1, "after 2 s the give goes out anyway");
        Check.Eq(FakeHost.Gives[0].Item, Carapace, "the stack that would not read");
        Check.Eq(FakeHost.Gives[0].Amount, 0, "as a plain give (it was a give-all)");
        Check.True(FakeHost.Logs.Any(l => l.Contains("unreadable for", StringComparison.Ordinal)), "and the log says why");
        w.Tick();
        Check.Eq(FakeHost.Gives.Count, 2, "the queue moved on to the next item");
        Check.Eq(FakeHost.Gives[1].Item, Mote1, "the one behind it");

        // A counted give keeps its explicit amount.
        var w2 = new World();
        w2.Now = 1000;
        w2.AddStack(Carapace, "Olthoi Slayer Carapace", 5);
        FakeHost.Ints[(Carapace, (uint)(int)LongValueKey.MaxStackSize)] = 100;
        FakeHost.Ints.Remove((Carapace, 12));
        FakeHost.GiveHandler = (_, _, _) => { };
        w2.Call("EnqueueGive", Carapace, Npc, 3);
        w2.Tick();
        w2.Tick(2001);
        Check.Eq(FakeHost.Gives.Count, 1, "a counted give goes out after 2 s too");
        Check.Eq(FakeHost.Gives[0].Amount, 3, "with its explicit amount");
    }

    // Review of PR 18 (follow-up): after the 2 s fallback, an amount of 1 used to go out as a plain give, which hands
    // over the WHOLE object: a "give 1" of a stack that is really 99 would have sent all 99.
    private static void UnreadableStackNeverGivesWholeForACount()
    {
        var w = new World();
        w.Now = 1000;
        w.AddStack(Carapace, "Olthoi Slayer Carapace", 99);
        FakeHost.Ints[(Carapace, (uint)(int)LongValueKey.MaxStackSize)] = 100;
        FakeHost.Ints.Remove((Carapace, 12));   // really 99, but the count will not read
        int sent = 0;
        FakeHost.GiveHandler = (_, _, amount) => sent += amount;   // the server hands over what is asked
        w.Call("EnqueueGive", Carapace, Npc, 1);
        w.Tick();
        w.Tick(1500);
        Check.Eq(FakeHost.Gives.Count, 0, "still waiting inside 2 s");
        w.Tick(600);
        Check.Eq(FakeHost.Gives.Count, 1, "after 2 s the give goes out");
        Check.Eq(FakeHost.Gives[0].Amount, 1, "as an explicit 1, never as a whole-object give (0)");
        Check.Eq(sent, 1, "one item reached the target, not the stack");

        // A re-send (no answer) keeps the explicit amount too.
        w.Tick(3001);
        Check.Eq(FakeHost.Gives.Count, 2, "no answer: re-sent");
        Check.Eq(FakeHost.Gives[1].Amount, 1, "the re-send is still an explicit 1");

        // A stackable the cache has no MaxStackSize for (unknown, so no wait): the explicit count still goes out as asked.
        var w2 = new World();
        w2.AddStack(Carapace, "Olthoi Slayer Carapace", 99);
        FakeHost.Ints.Remove((Carapace, 12));
        FakeHost.GiveHandler = (_, _, _) => { };
        w2.Call("EnqueueGive", Carapace, Npc, 1);
        w2.Tick();
        Check.Eq(FakeHost.Gives.Count, 1, "unknown MaxStackSize: no wait");
        Check.Eq(FakeHost.Gives[0].Amount, 1, "and an explicit 1");

        // A give-all with the count unreadable is the one that goes out whole, and a re-send of it stays whole.
        var w3 = new World();
        w3.Now = 1000;
        w3.AddStack(Carapace, "Olthoi Slayer Carapace", 99);
        FakeHost.Ints[(Carapace, (uint)(int)LongValueKey.MaxStackSize)] = 100;
        FakeHost.Ints.Remove((Carapace, 12));
        FakeHost.GiveHandler = (_, _, _) => { };
        w3.Call("EnqueueGive", Carapace, Npc, 0);
        w3.Tick();
        w3.Tick(2001);
        Check.Eq(FakeHost.Gives.Count, 1, "give-all: out after 2 s");
        Check.Eq(FakeHost.Gives[0].Amount, 0, "as a whole-object give");
        w3.Tick(3001);
        Check.Eq(FakeHost.Gives.Count, 2, "no answer: re-sent");
        Check.Eq(FakeHost.Gives[1].Amount, 0, "still a give-all, not an explicit 1");
    }

    private static void CountOneStackableWithoutCountGetsThrough()
    {
        var w = new World();
        w.Now = 1000;
        w.AddStack(Carapace, "Olthoi Slayer Carapace", 1);
        FakeHost.Ints[(Carapace, (uint)(int)LongValueKey.MaxStackSize)] = 100;
        FakeHost.Ints.Remove((Carapace, 12));   // a stack of one: the property is simply absent
        FakeHost.GiveHandler = (item, target, _) => FakeHost.Containers[item] = target;   // the server takes it
        w.Call("EnqueueGive", Carapace, Npc, 1);
        w.Call("EnqueueGive", Mote1, Npc, 1);
        w.Tick();
        w.Tick(2100);
        Check.Eq(FakeHost.Gives.Count, 1, "the lone stackable goes out after the read wait");
        Check.Eq(FakeHost.Gives[0].Item, Carapace, "it is the stack that would not read");
        Check.Eq(FakeHost.Gives[0].Amount, 1, "as an explicit 1");
        w.Tick(); w.Tick();
        Check.Eq(FakeHost.Gives.Count, 2, "it left the pack: counted given, the queue moved on");
        Check.False(w.ChatHas("didn't take"), "and nothing was skipped");
    }

    private static void GiveAllQueuesWholeStack()
    {
        var w = new World();
        w.AddStack(Carapace, "Olthoi Slayer Carapace", 7);
        FakeHost.GiveHandler = (item, target, _) => FakeHost.Containers[item] = target;
        w.Run("/ub give Olthoi Slayer Carapace to Town Crier");
        Check.Eq(FakeHost.Gives.Count, 1, "one give");
        Check.Eq(FakeHost.Gives[0].Amount, 0, "the whole stack");
    }

    // Review of PR 18: a target that is never free (a player in a trade) was re-sent every 300 ms for ever,
    // printing the server's "too busy" line in that player's chat about three times a second.
    private static void BusyGivesUpAfterAMinute()
    {
        var w = new World();
        w.Now = 1000;
        FakeHost.GiveLands = false;
        w.Call("EnqueueGive", Mote1, Npc, 1);
        w.Call("EnqueueGive", Mote2, Npc, 1);
        w.Tick();
        long start = w.Now;
        int guard = 0;
        while (FakeHost.Gives.Count(g => g.Item == Mote2) == 0 && guard++ < 1000)
        {
            w.Chat("You're too busy!");
            w.Tick(0);
            w.Tick(300);
        }
        long waited = w.Now - start;
        Check.True(FakeHost.Gives.Any(g => g.Item == Mote2), "after the cap the next stack is sent");
        Check.True(waited >= 59000 && waited <= 61500, $"given up after about a minute of busy, not before ({waited} ms)");
        Check.True(FakeHost.Gives.Count(g => g.Item == Mote1) <= 210, "the first stack was not re-sent past the cap");
        Check.True(w.ChatHas("stayed busy"), "the player is told");
        FakeHost.Containers[Mote2] = Npc;
        w.Tick();
        Check.True(w.ChatHas("1 item(s) given, 1 stack(s) skipped"), "the summary counts the busy skip");
        Check.True(w.ChatHas("busy too long"), "with its reason");
    }

    // Review of PR 18: when the no-answer re-send could not go out (unreadable stack) the try was not
    // counted, so GiveMaxTries + 1 sends could happen.
    private static void FailedResendCountsAsATry()
    {
        var w = new World();
        w.Now = 1000;
        w.AddStack(Carapace, "Olthoi Slayer Carapace", 5);
        FakeHost.Ints[(Carapace, (uint)(int)LongValueKey.MaxStackSize)] = 100;
        FakeHost.GiveHandler = (_, _, _) => { };   // never answers
        w.Call("EnqueueGive", Carapace, Npc, 5);
        w.Tick();
        Check.Eq(FakeHost.Gives.Count, 1, "sent");
        FakeHost.Ints.Remove((Carapace, 12));   // the read fails when the re-send is due
        w.Tick(3001);
        Check.Eq(FakeHost.Gives.Count, 1, "the re-send could not go out");
        FakeHost.Ints[(Carapace, 12)] = 5;
        w.Tick();
        Check.Eq(FakeHost.Gives.Count, 2, "it goes out on the next tick that reads");
        w.Tick(3001);
        Check.Eq(FakeHost.Gives.Count, 2, "two tries in all (GiveMaxTries), then it gives up");
        Check.True(w.ChatHas("didn't take"), "and says so");
    }
}

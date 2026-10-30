using System;
using System.Collections.Generic;
using System.Linq;
using RynthCore.Plugin.RynthAi;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.Plugin.RynthAi.Loot;
using RynthCore.RynthAiHostTests.Fakes;
using RynthCore.RynthAiTests.Harness;

namespace RynthCore.RynthAiHostTests.Tests;

// Two tester reports (2026-10-02):
//  A. Jewellery looted for salvage got WORN when its slot was empty. The corpse pickup was a
//     UseObject, and a use on a wearable item outside the pack is get-and-wield. Pickups are a
//     move into a pack now, and salvage never takes off anything the character wears.
//  B. Salvage skipped a good chunk of items, which sat in the pack. Items queued before they
//     reached the pack were counted as salvaged, a lost pickup confirm meant the item was never
//     queued, no UST or three failed tries dropped items for good, and one item went per panel
//     cycle. Items are tracked until salvaged or gone, go in batches, and every batch logs what
//     was salvaged and why anything was skipped.
internal static class LootSalvageTests
{
    private const uint Player = 0x50000C01;
    private const uint Ust = 0x80000F01;
    private const uint SidePack = 0x80000F02;
    private const uint Ring = 0x80000A01;
    private const uint Gem = 0x80000A02;
    private const uint ItemBase = 0x80000B00;
    private const uint UstWcid = 20646;
    private const uint ItemTypeJewelry = 0x8, ItemTypeGem = 0x800, ItemTypeContainer = 0x200, ItemTypeArmor = 0x2;
    private static readonly string TestLogDirectory =
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), "RynthAiHostTests", "Diagnostics");

    public static void Register(Runner r)
    {
        r.Add("loot pickup: a corpse item is moved into the main pack, never used (no get-and-wield)", PickupMovesNeverUses);
        r.Add("loot pickup: a full main pack sends the item to a side pack with room", PickupFullMainPackGoesToSidePack);
        r.Add("loot pickup: no move call -> wearables are left, other items are used", PickupWithoutMoveCall);
        r.Add("salvage: a worn item is left alone and never taken off", SalvageLeavesWornItem);
        r.Add("salvage: a corpse's items go in one batch and all are salvaged", SalvageOneBatch);
        r.Add("salvage: more than one batch of items keeps going until all are salvaged", SalvageSeveralBatches);
        r.Add("salvage: an item queued before it reaches the pack waits, then is salvaged", SalvageWaitsForArrival);
        r.Add("salvage: a pickup whose confirm was lost is still salvaged (tracked + sweep)", SalvageLostConfirm);
        r.Add("salvage: no UST keeps the items until one is in the pack", SalvageNoUstKeepsItems);
        r.Add("salvage: no UST - HasUst false, no combine sweep; a UST in the pack turns both on", SalvageNoUstNoSweep);
        r.Add("salvage: skipped items are logged with their reason, once per batch", SalvageSkipReasonsLogged);
        r.Add("salvage: an item that keeps failing rests, retries, and is given up with a log line", SalvageGiveUpIsBounded);
        // Salvage without the panel (engine API v82 SalvageDirect, 2026-10-12).
        r.Add("salvage without the panel: one request with every item, the panel never opens", DirectOneRequest);
        r.Add("salvage without the panel: setting off, or an engine without it -> the panel", DirectNotChosenWhenOffOrUnsupported);
        r.Add("salvage without the panel: batches of up to 50 items, the rest in the next request", DirectBatching);
        r.Add("salvage without the panel: worn, retained, Items-list items and the UST never go in", DirectFiltersStillApply);
        r.Add("salvage without the panel: matching under-full bags ride along and merge", DirectBagsRideAlong);
        r.Add("salvage without the panel: a request that changes nothing is tried once through the panel", DirectFallsBackToPanelOnce);
        r.Add("salvage without the panel: an unbound sender -> the panel, and off for the session", DirectUnboundSenderFallsBack);
        r.Add("salvage without the panel: after 3 batches the panel had to save, the session uses the panel", DirectTurnsOffAfterRepeatedFallbacks);
        r.Add("salvage without the panel: no UST -> no request, no panel, items kept", DirectNoUstDoesNothing);
        r.Add("salvage without the panel: a failing item is still bounded (rests, gives up)", DirectGiveUpIsBounded);
        r.Add("salvage without the panel: the combine sweep sends bag groups as requests too", DirectCombineSweep);
        r.Add("salvage without the panel: a combine request that merges nothing is tried once through the panel", DirectCombineFallsBackToPanel);
    }

    // ── Loot pickup (bug A) ───────────────────────────────────────────────────

    private static void PickupMovesNeverUses()
    {
        FakeHost.Reset();
        FakeHost.SalvageCalls = true;
        FakeHost.Names[Ring] = "Gold Ring";
        FakeHost.ItemTypes[Ring] = ItemTypeJewelry;
        FakeHost.Ints[(Ring, 9)] = 0x40000; // ValidLocations: a finger
        var host = FakeHost.Create(Player);
        var cache = FakeHost.MakeCache(host, Player, new[] { Ring });

        var result = LootPickup.Send(host, cache, Ring, Player, out uint dest);

        Check.Eq(result, LootPickupResult.Moved, "a move was sent");
        Check.Eq(dest, Player, "into the main pack");
        Check.Eq(FakeHost.ExternalMoves.Count, 1, "one MoveItemExternal");
        Check.Eq(FakeHost.ExternalMoves[0], (Ring, Player, 0), "the ring, to the player, whole stack");
        Check.Eq(FakeHost.Uses.Count, 0, "no UseObject (a use on a corpse ring wields it)");
        Check.Eq(FakeHost.Wields.Count, 0, "no WieldItem");
    }

    private static void PickupFullMainPackGoesToSidePack()
    {
        FakeHost.Reset();
        FakeHost.SalvageCalls = true;
        var ids = new List<uint>();
        for (uint i = 0; i < 102; i++)
        {
            uint id = ItemBase + i;
            FakeHost.Names[id] = $"Junk {i}";
            FakeHost.ItemTypes[id] = ItemTypeGem;
            FakeHost.Containers[id] = Player;
            ids.Add(id);
        }
        FakeHost.Names[SidePack] = "Backpack";
        FakeHost.ItemTypes[SidePack] = ItemTypeContainer;
        FakeHost.Ints[(SidePack, 6)] = 24; // ItemsCapacity
        FakeHost.Containers[SidePack] = Player;
        ids.Add(SidePack);
        FakeHost.Names[Ring] = "Gold Ring";
        FakeHost.ItemTypes[Ring] = ItemTypeJewelry;
        ids.Add(Ring);
        var host = FakeHost.Create(Player);
        var cache = FakeHost.MakeCache(host, Player, ids);

        var result = LootPickup.Send(host, cache, Ring, Player, out uint dest);

        Check.Eq(result, LootPickupResult.Moved, "a move was sent");
        Check.Eq(dest, SidePack, "the main pack is full: the side pack");
        Check.Eq(FakeHost.Uses.Count, 0, "no UseObject");
    }

    private static void PickupWithoutMoveCall()
    {
        FakeHost.Reset();
        FakeHost.WeaponCalls = true; // UseObject, but no MoveItemExternal
        FakeHost.Names[Ring] = "Gold Ring";
        FakeHost.ItemTypes[Ring] = ItemTypeJewelry;
        FakeHost.Names[Gem] = "Black Opal";
        FakeHost.ItemTypes[Gem] = ItemTypeGem;
        var host = FakeHost.Create(Player);
        var cache = FakeHost.MakeCache(host, Player, new[] { Ring, Gem });

        Check.Eq(LootPickup.Send(host, cache, Ring, Player, out _), LootPickupResult.Refused, "a ring is left");
        Check.Eq(FakeHost.Uses.Count, 0, "the ring is never used");
        Check.Eq(LootPickup.Send(host, cache, Gem, Player, out _), LootPickupResult.Used, "a gem can't be worn: used");
        Check.Eq(FakeHost.Uses.Count, 1, "one use, the gem");
        Check.True(LootPickup.CouldBeWorn(null), "an unknown item counts as wearable");
    }

    // ── Salvage (bugs A and B) ────────────────────────────────────────────────

    /// <summary>A salvage manager over a fake world: a UST plus <paramref name="items"/> in the pack.
    /// Executing the panel removes what was added from the world, like the server does.</summary>
    private sealed class World
    {
        public readonly RynthCore.PluginSdk.RynthCoreHost Host;
        public readonly WorldObjectCache Cache;
        public readonly LegacyUiSettings Settings = new();
        public readonly SalvageManager Mgr;
        public long Now = 1_000_000;
        public readonly HashSet<uint> Unsalvageable = new();

        /// <summary>Direct worlds: the server ignores requests without the panel (they change nothing).</summary>
        public bool ServerIgnoresDirect;
        /// <summary>Salvage bags the fake server made (one per request that salvaged something).</summary>
        public readonly List<uint> NewBags = new();
        private uint _nextBag = 0x80000E00;

        public World(IEnumerable<uint> items, bool withUst = true, Action? beforeCreate = null, bool direct = false)
        {
            FakeHost.Reset();
            FakeHost.SalvageCalls = true;
            FakeHost.SalvageDirectCalls = direct;
            var ids = new List<uint>();
            if (withUst) AddUst(ids);
            foreach (uint id in items)
            {
                if (!FakeHost.Names.ContainsKey(id)) FakeHost.Names[id] = $"Item {id - ItemBase}";
                if (!FakeHost.ItemTypes.ContainsKey(id)) FakeHost.ItemTypes[id] = ItemTypeArmor;
                FakeHost.Ints[(id, 131)] = 64; // MaterialType
                ids.Add(id);
            }
            beforeCreate?.Invoke();
            Host = FakeHost.Create(Player);
            // SalvageManager logs through RynthLog (LogCat.Salvage), which forwards to the host it
            // is bound to; bind it to this fake so FakeHost.Logs sees the [Salvage] lines. A temp
            // folder keeps the test's diagnostics.json and log files out of the real install.
            RynthLog.Init(Host, TestLogDirectory);
            Cache = FakeHost.MakeCache(Host, Player, ids);
            Settings.EnableCombineSalvage = false; // bag merging has its own tests in game
            Settings.SalvageOpenDelayFirstMs = 400; Settings.SalvageOpenDelayFastMs = 50;
            Settings.SalvageAddDelayFirstMs = 600; Settings.SalvageAddDelayFastMs = 50;
            Settings.SalvageSalvageDelayMs = 50;
            Settings.SalvageResultDelayFirstMs = 1000; Settings.SalvageResultDelayFastMs = 250;
            Mgr = new SalvageManager(Host, Settings, Cache) { Clock = () => Now };
            FakeHost.OnSalvageExecute = added => ServerSalvages(Ust, added);
            // The 0x027D request without the panel reaches the same server code (ACE HandleSalvaging).
            FakeHost.OnSalvageDirect = (tool, ids) => { if (!ServerIgnoresDirect) ServerSalvages(tool, ids); };
        }

        /// <summary>What ACE does with a salvage request: a carried UST, then each salvageable id is
        /// consumed (salvage bags too, merged), and one new bag holds the result.</summary>
        private void ServerSalvages(uint tool, List<uint> ids)
        {
            if (!InWorld(tool)) { Mgr.OnChatWindowText("You do not own that salvage tool!"); return; }
            int consumed = 0;
            foreach (uint id in ids)
            {
                if (Unsalvageable.Contains(id) || !InWorld(id)) continue;
                FakeHost.Names.Remove(id);
                Cache.OnDeleteObject(id);
                consumed++;
            }
            if (consumed == 0) return;
            uint bag = _nextBag++;
            NewBags.Add(bag);
            FakeHost.Names[bag] = "Salvage (100)";
            FakeHost.Ints[(bag, 131)] = 64;
            _ = Cache[unchecked((int)bag)];
            Mgr.OnChatWindowText("You obtain 100 Steel (ws 7.00) using your knowledge of Salvaging.");
        }

        /// <summary>An under-full salvage bag of material 64 in the pack.</summary>
        public void AddBag(uint id, int structure)
        {
            FakeHost.Names[id] = $"Salvage ({structure})";
            FakeHost.Ints[(id, 131)] = 64;
            FakeHost.Ints[(id, 92)] = structure;
            FakeHost.Ints[(id, 91)] = 100;
            _ = Cache[unchecked((int)id)];
        }

        public static void AddUst(List<uint> ids)
        {
            FakeHost.Names[Ust] = "Salvaging Ust";
            FakeHost.Wcids[Ust] = UstWcid;
            ids.Add(Ust);
        }

        /// <summary>The item lands in the pack (the cache learns about it).</summary>
        public void Arrive(uint id, string name)
        {
            FakeHost.Names[id] = name;
            FakeHost.ItemTypes[id] = ItemTypeArmor;
            FakeHost.Ints[(id, 131)] = 64;
            _ = Cache[unchecked((int)id)];
        }

        public void Run(long ms, int step = 50)
        {
            for (long t = 0; t < ms; t += step) { Now += step; Mgr.OnTick(0); }
        }

        public bool InWorld(uint id) => FakeHost.Names.ContainsKey(id);
        public IEnumerable<string> SalvageLogs => FakeHost.Logs.Where(l => l.StartsWith("[Salvage]", StringComparison.Ordinal));
    }

    private static uint[] Items(int n) => Enumerable.Range(0, n).Select(i => ItemBase + (uint)i).ToArray();

    private static void SalvageLeavesWornItem()
    {
        var w = new World(new[] { Ring }, beforeCreate: () =>
        {
            FakeHost.Names[Ring] = "Gold Ring";
            FakeHost.ItemTypes[Ring] = ItemTypeJewelry;
            FakeHost.Wielded[Ring] = 0x40000; // on a finger
        });
        w.Mgr.EnqueueItem(Ring);
        w.Run(10_000);

        Check.False(FakeHost.SalvageAdds.Contains(Ring), "the worn ring never goes into the salvage panel");
        Check.Eq(FakeHost.Wields.Count, 0, "nothing wielded");
        Check.Eq(FakeHost.Moves.Count, 0, "nothing taken off (no move into a pack)");
        Check.Eq(FakeHost.ExternalMoves.Count, 0, "nothing moved out");
        Check.True(w.InWorld(Ring), "the ring is still there");
        Check.True(FakeHost.Chat.Any(c => c.Contains("wearing", StringComparison.Ordinal)), "chat says it was left alone");
        Check.True(w.SalvageLogs.Any(l => l.Contains("worn", StringComparison.Ordinal) && l.Contains("Gold Ring", StringComparison.Ordinal)),
            "the log names the item and the reason");
        Check.Eq(w.Mgr.GetStateSnapshot().TrackedItems, 0, "no longer tracked");
    }

    private static void SalvageOneBatch()
    {
        var items = Items(12);
        var w = new World(items);
        foreach (uint id in items) w.Mgr.EnqueueItem(id);
        w.Run(10_000);

        Check.Eq(FakeHost.Uses.Count(u => u == Ust), 1, "the UST is used once");
        Check.Eq(FakeHost.SalvageExecutes.Count, 1, "one Salvage press");
        Check.Eq(FakeHost.SalvageExecutes[0].Count, 12, "with all twelve items");
        Check.True(items.All(id => !w.InWorld(id)), "every item salvaged");
        Check.True(w.SalvageLogs.Any(l => l.Contains("Batch 1: salvaged 12/12", StringComparison.Ordinal)), "one summary line");
        Check.Eq(w.Mgr.GetStateSnapshot().ItemsSalvaged, 12, "session total");
        Check.False(w.Mgr.IsBusy, "idle afterwards");
    }

    private static void SalvageSeveralBatches()
    {
        var items = Items(SalvageManager.MaxBatchItems + 5);
        var w = new World(items);
        foreach (uint id in items) w.Mgr.EnqueueItem(id);
        w.Run(20_000);

        Check.Eq(FakeHost.SalvageExecutes.Count, 2, "two Salvage presses");
        Check.Eq(FakeHost.SalvageExecutes[0].Count, SalvageManager.MaxBatchItems, "a full first batch");
        Check.Eq(FakeHost.SalvageExecutes[1].Count, 5, "the rest in the second");
        Check.True(items.All(id => !w.InWorld(id)), "every item salvaged");
        Check.Eq(FakeHost.Uses.Count(u => u == Ust), 2, "the UST is used again for the second batch");
    }

    private static void SalvageWaitsForArrival()
    {
        uint late = ItemBase + 50;
        var w = new World(Array.Empty<uint>());
        w.Mgr.EnqueueItem(late);  // confirm fired, but the item isn't in the pack yet
        w.Run(3_000);
        Check.Eq(FakeHost.SalvageExecutes.Count, 0, "nothing salvaged while the item is on its way");
        Check.Eq(w.Mgr.GetStateSnapshot().ItemsSalvaged, 0, "not counted as salvaged (the old code counted it)");

        w.Arrive(late, "Steel Helm");
        w.Run(10_000);
        Check.Eq(FakeHost.SalvageExecutes.Count, 1, "salvaged once it arrived");
        Check.False(w.InWorld(late), "gone");
    }

    private static void SalvageLostConfirm()
    {
        uint item = ItemBase + 60;
        var w = new World(Array.Empty<uint>());
        w.Mgr.NoteLootedForSalvage(item, "Steel Gauntlets"); // pickup sent; the confirm never comes
        w.Run(2_000);
        w.Arrive(item, "Steel Gauntlets");
        w.Run(10_000);

        Check.Eq(FakeHost.SalvageExecutes.Count, 1, "the sweep queued and salvaged it");
        Check.False(w.InWorld(item), "gone");
        Check.True(w.SalvageLogs.Any(l => l.Contains("Sweep: queued 1", StringComparison.Ordinal)), "the sweep says so");
    }

    private static void SalvageNoUstKeepsItems()
    {
        var items = Items(3);
        var w = new World(items, withUst: false);
        foreach (uint id in items) w.Mgr.EnqueueItem(id);
        w.Run(5_000);
        Check.Eq(FakeHost.SalvageExecutes.Count, 0, "no UST: nothing salvaged");
        Check.Eq(FakeHost.Chat.Count(c => c.Contains("no UST", StringComparison.Ordinal)), 1, "one chat warning");
        Check.Eq(w.Mgr.GetStateSnapshot().QueueCount, 3, "the items are kept (they used to be dropped)");

        var ids = new List<uint>();
        World.AddUst(ids);
        _ = w.Cache[unchecked((int)Ust)];
        w.Run(15_000);
        Check.Eq(FakeHost.SalvageExecutes.Count, 1, "salvaged once a UST is there");
        Check.True(items.All(id => !w.InWorld(id)), "every item salvaged");
    }

    private static void SalvageNoUstNoSweep()
    {
        var w = new World(Items(0), withUst: false);
        w.Settings.EnableCombineSalvage = true;
        Check.False(w.Mgr.HasUst(), "no UST carried");
        w.Run(65_000, step: 500);
        Check.False(w.SalvageLogs.Any(l => l.Contains("Combine sweep", StringComparison.Ordinal)), "no combine sweep without a UST");
        Check.Eq(FakeHost.Uses.Count, 0, "nothing used");

        var ids = new List<uint>();
        World.AddUst(ids);
        _ = w.Cache[unchecked((int)Ust)];
        w.Run(4_000, step: 500);
        Check.True(w.Mgr.HasUst(), "a UST in the pack is seen (within the 3 s cache)");
        w.Run(31_000, step: 500);
        Check.True(w.SalvageLogs.Any(l => l.Contains("Combine sweep", StringComparison.Ordinal)), "the combine sweep runs again");
    }

    private static void SalvageSkipReasonsLogged()
    {
        uint good = ItemBase + 1, retained = ItemBase + 2, listed = ItemBase + 3;
        var w = new World(new[] { good, retained, listed }, beforeCreate: () =>
        {
            FakeHost.Names[good] = "Iron Breastplate";
            FakeHost.Names[retained] = "Heirloom Coat";
            FakeHost.Bools[(retained, 91)] = true;
            FakeHost.Names[listed] = "Favourite Sword";
        });
        w.Settings.ItemRules.Add(new ItemRule { Id = unchecked((int)listed), Name = "Favourite Sword" });
        w.Mgr.EnqueueItem(good);
        w.Mgr.EnqueueItem(retained);
        w.Mgr.EnqueueItem(listed);
        w.Run(10_000);

        Check.Eq(FakeHost.SalvageExecutes.Count, 1, "one batch");
        Check.Eq(string.Join(",", FakeHost.SalvageExecutes[0]), good.ToString(), "only the good item went in");
        Check.True(w.InWorld(retained) && w.InWorld(listed), "the skipped items are untouched");
        var batchLines = w.SalvageLogs.Where(l => l.Contains("Batch 1:", StringComparison.Ordinal)).ToList();
        Check.Eq(batchLines.Count, 1, "one line for the batch");
        string line = batchLines.Count > 0 ? batchLines[0] : "";
        Check.True(line.Contains("salvaged 1/1 (Iron Breastplate)", StringComparison.Ordinal), "what was salvaged");
        Check.True(line.Contains("retained: Heirloom Coat", StringComparison.Ordinal), "the retained item and why");
        Check.True(line.Contains("on the Items list: Favourite Sword", StringComparison.Ordinal), "the Items-list item and why");
        Check.Eq(w.Mgr.GetStateSnapshot().ItemsSkipped, 2, "two skipped");
    }

    private static void SalvageGiveUpIsBounded()
    {
        uint stubborn = ItemBase + 7;
        var w = new World(new[] { stubborn });
        w.Unsalvageable.Add(stubborn); // the server never consumes it
        w.Mgr.EnqueueItem(stubborn);

        w.Run(30_000);
        int firstRound = FakeHost.SalvageExecutes.Count;
        Check.Eq(firstRound, 3, "three tries, then it rests");
        Check.True(w.SalvageLogs.Any(l => l.Contains("trying again in 60s", StringComparison.Ordinal)), "the rest is logged");

        w.Run(10 * 60_000, step: 100);
        Check.Eq(FakeHost.SalvageExecutes.Count, 3 * SalvageManager.MaxGiveUpRounds, "three rounds of three tries, no more");
        Check.True(w.SalvageLogs.Any(l => l.Contains("Giving up on", StringComparison.Ordinal)), "giving up is logged");
        Check.Eq(w.Mgr.GetStateSnapshot().TrackedItems, 0, "no longer tracked");
        int logLines = w.SalvageLogs.Count();
        Check.True(logLines < 25, $"bounded logging ({logLines} salvage lines over 10 minutes)");
    }

    // ── Salvage without the panel ─────────────────────────────────────────────

    private static int PanelOpens => FakeHost.Uses.Count(u => u == Ust);

    private static void DirectOneRequest()
    {
        var items = Items(12);
        var w = new World(items, direct: true);
        foreach (uint id in items) w.Mgr.EnqueueItem(id);
        w.Run(5_000);

        Check.Eq(FakeHost.SalvageDirects.Count, 1, "one request");
        Check.Eq(FakeHost.SalvageDirects[0].Tool, Ust, "with the UST");
        Check.Eq(FakeHost.SalvageDirects[0].Ids.Count, 12, "and all twelve items");
        Check.Eq(PanelOpens, 0, "the UST is never used (the panel never opens)");
        Check.Eq(FakeHost.SalvageAdds.Count + FakeHost.SalvageExecutes.Count, 0, "no panel calls");
        Check.True(items.All(id => !w.InWorld(id)), "every item salvaged");
        string line = w.SalvageLogs.FirstOrDefault(l => l.Contains("Batch 1", StringComparison.Ordinal)) ?? "";
        Check.True(line.Contains("Batch 1 without the panel: salvaged 12/12", StringComparison.Ordinal), "one summary line, saying how: " + line);
        Check.True(line.Contains("1 new salvage bag(s)", StringComparison.Ordinal), "the new bag is noticed");
        Check.True(line.Contains("using your knowledge of", StringComparison.Ordinal), "the server's line is quoted");
        var v = w.Mgr.GetStateSnapshot();
        Check.True(v.DirectOn && v.HasDirectApi, "the snapshot says it is on");
        Check.Eq(v.DirectRequests, 1, "one request counted");
        Check.False(w.Mgr.IsBusy, "idle afterwards");
    }

    private static void DirectNotChosenWhenOffOrUnsupported()
    {
        var items = Items(3);
        var w = new World(items, direct: true);
        w.Settings.SalvageWithoutPanel = false;
        foreach (uint id in items) w.Mgr.EnqueueItem(id);
        w.Run(10_000);
        Check.Eq(FakeHost.SalvageDirects.Count, 0, "setting off: no request without the panel");
        Check.Eq(FakeHost.SalvageExecutes.Count, 1, "the panel salvaged them");
        Check.Eq(w.Mgr.GetStateSnapshot().DirectOffReason, "setting off", "the snapshot says why");

        var w2 = new World(items, direct: false);   // an engine before v81: no SalvageDirectFn
        foreach (uint id in items) w2.Mgr.EnqueueItem(id);
        w2.Run(10_000);
        Check.Eq(FakeHost.SalvageExecutes.Count, 1, "older engine: the panel");
        Check.True(items.All(id => !w2.InWorld(id)), "every item salvaged");
        Check.Eq(w2.Mgr.GetStateSnapshot().DirectOffReason, "engine before API v82", "the snapshot says why");
    }

    private static void DirectBatching()
    {
        var items = Items(SalvageManager.MaxDirectBatchItems + 10);
        var w = new World(items, direct: true);
        foreach (uint id in items) w.Mgr.EnqueueItem(id);
        w.Run(10_000);

        Check.Eq(FakeHost.SalvageDirects.Count, 2, "two requests");
        Check.Eq(FakeHost.SalvageDirects[0].Ids.Count, SalvageManager.MaxDirectBatchItems, "a full first request (50)");
        Check.Eq(FakeHost.SalvageDirects[1].Ids.Count, 10, "the rest in the second");
        Check.True(FakeHost.SalvageDirects.All(r => r.Ids.Count <= RynthCore.PluginSdk.RynthCoreHost.MaxSalvageDirectItems), "never over the engine's cap");
        Check.Eq(FakeHost.SalvageDirects.SelectMany(r => r.Ids).Distinct().Count(), items.Length, "each item sent once");
        Check.True(items.All(id => !w.InWorld(id)), "every item salvaged");
        Check.Eq(PanelOpens, 0, "no panel");
    }

    private static void DirectFiltersStillApply()
    {
        uint good = ItemBase + 1, retained = ItemBase + 2, listed = ItemBase + 3;
        var w = new World(new[] { good, retained, listed, Ring }, direct: true, beforeCreate: () =>
        {
            FakeHost.Names[good] = "Iron Breastplate";
            FakeHost.Names[retained] = "Heirloom Coat";
            FakeHost.Bools[(retained, 91)] = true;
            FakeHost.Names[listed] = "Favourite Sword";
            FakeHost.Names[Ring] = "Gold Ring";
            FakeHost.ItemTypes[Ring] = ItemTypeJewelry;
            FakeHost.Wielded[Ring] = 0x40000;
        });
        w.Settings.ItemRules.Add(new ItemRule { Id = unchecked((int)listed), Name = "Favourite Sword" });
        foreach (uint id in new[] { good, retained, listed, Ring, Ust }) w.Mgr.EnqueueItem(id);
        w.Run(5_000);

        Check.Eq(FakeHost.SalvageDirects.Count, 1, "one request");
        Check.Eq(string.Join(",", FakeHost.SalvageDirects[0].Ids), good.ToString(), "only the good item went in");
        Check.True(w.InWorld(retained) && w.InWorld(listed) && w.InWorld(Ring) && w.InWorld(Ust), "the skipped items are untouched");
        string line = w.SalvageLogs.FirstOrDefault(l => l.Contains("Batch 1", StringComparison.Ordinal)) ?? "";
        Check.True(line.Contains("retained: Heirloom Coat", StringComparison.Ordinal), "the retained item and why");
        Check.True(line.Contains("on the Items list: Favourite Sword", StringComparison.Ordinal), "the Items-list item and why");
        Check.True(line.Contains("it is the UST", StringComparison.Ordinal), "the UST and why");
        Check.True(w.SalvageLogs.Any(l => l.Contains("worn", StringComparison.Ordinal) && l.Contains("Gold Ring", StringComparison.Ordinal)), "the worn ring and why");
    }

    private static void DirectBagsRideAlong()
    {
        uint item = ItemBase + 1, bagA = 0x80000D01, bagB = 0x80000D02, fullBag = 0x80000D03;
        var w = new World(new[] { item }, direct: true);
        w.AddBag(bagA, 30);
        w.AddBag(bagB, 45);
        w.AddBag(fullBag, 100);
        w.Mgr.EnqueueItem(item);
        w.Run(5_000);

        Check.Eq(FakeHost.SalvageDirects.Count, 1, "one request");
        var ids = FakeHost.SalvageDirects[0].Ids;
        Check.True(ids[0] == item && ids.Contains(bagA) && ids.Contains(bagB), "the item, then the two under-full bags");
        Check.False(ids.Contains(fullBag), "a full bag stays out");
        Check.True(!w.InWorld(bagA) && !w.InWorld(bagB) && w.InWorld(fullBag), "the under-full bags merged, the full one untouched");
        string line = w.SalvageLogs.FirstOrDefault(l => l.Contains("Batch 1", StringComparison.Ordinal)) ?? "";
        Check.True(line.Contains("2/2 bag(s) merged in", StringComparison.Ordinal), "the log says the bags merged: " + line);
    }

    private static void DirectFallsBackToPanelOnce()
    {
        var items = Items(4);
        var w = new World(items, direct: true) { ServerIgnoresDirect = true };
        foreach (uint id in items) w.Mgr.EnqueueItem(id);
        w.Run(15_000);

        Check.Eq(FakeHost.SalvageDirects.Count, 1, "one request without the panel");
        Check.Eq(PanelOpens, 1, "then the panel, once");
        Check.Eq(FakeHost.SalvageExecutes.Count, 1, "one Salvage press");
        Check.Eq(FakeHost.SalvageExecutes[0].Count, 4, "with the same four items");
        Check.True(items.All(id => !w.InWorld(id)), "the panel salvaged them");
        Check.True(w.SalvageLogs.Any(l => l.Contains("changed nothing", StringComparison.Ordinal) && l.Contains("no salvage reply", StringComparison.Ordinal)
                                          && l.Contains("once through the panel", StringComparison.Ordinal)), "the log says why it fell back");
        Check.True(w.SalvageLogs.Any(l => l.Contains("Batch 1 through the panel (the request without the panel changed nothing): salvaged 4/4", StringComparison.Ordinal)),
            "the batch line says how it went");
        var v = w.Mgr.GetStateSnapshot();
        Check.Eq(v.DirectFallbacks, 1, "one fallback counted");
        Check.True(v.DirectOn, "one fallback doesn't turn it off");
        Check.Eq(v.ItemsSalvaged, 4, "session total");
    }

    private static void DirectUnboundSenderFallsBack()
    {
        var items = Items(3);
        var w = new World(items, direct: true);
        FakeHost.SalvageDirectAnswer = -1;   // the engine couldn't bind Event_CreateTinkeringTool
        foreach (uint id in items) w.Mgr.EnqueueItem(id);
        w.Run(10_000);

        Check.Eq(FakeHost.SalvageExecutes.Count, 1, "the panel took the batch");
        Check.True(items.All(id => !w.InWorld(id)), "every item salvaged");
        Check.True(w.SalvageLogs.Any(l => l.Contains("wasn't sent", StringComparison.Ordinal) && l.Contains("isn't bound", StringComparison.Ordinal)), "the log says why");
        var v = w.Mgr.GetStateSnapshot();
        Check.False(v.DirectOn, "off for the session");
        Check.True(v.DirectOffReason.Contains("isn't bound", StringComparison.Ordinal), "with the reason");

        foreach (uint id in Items(2).Select(i => i + 100)) { w.Arrive(id, "Steel Helm"); w.Mgr.EnqueueItem(id); }
        w.Run(10_000);
        Check.Eq(FakeHost.SalvageDirects.Count, 1, "no second request without the panel");
        Check.Eq(FakeHost.SalvageExecutes.Count, 2, "the next batch goes straight to the panel");
    }

    private static void DirectTurnsOffAfterRepeatedFallbacks()
    {
        var w = new World(Array.Empty<uint>(), direct: true) { ServerIgnoresDirect = true };
        for (int round = 0; round < SalvageManager.DirectOffAfterFailures; round++)
        {
            uint id = ItemBase + 200 + (uint)round;
            w.Arrive(id, $"Steel Helm {round}");
            w.Mgr.EnqueueItem(id);
            w.Run(10_000);
            Check.False(w.InWorld(id), $"round {round + 1}: salvaged through the panel");
        }
        Check.Eq(FakeHost.SalvageDirects.Count, SalvageManager.DirectOffAfterFailures, "one request per round");
        Check.False(w.Mgr.GetStateSnapshot().DirectOn, "then off for the session");
        Check.True(FakeHost.Chat.Any(c => c.Contains("using the salvage panel for the rest of the session", StringComparison.Ordinal)), "chat says so");

        uint next = ItemBase + 300;
        w.Arrive(next, "Steel Helm 9");
        w.Mgr.EnqueueItem(next);
        w.Run(10_000);
        Check.Eq(FakeHost.SalvageDirects.Count, SalvageManager.DirectOffAfterFailures, "no more requests without the panel");
        Check.False(w.InWorld(next), "the panel salvaged the next one");
    }

    private static void DirectNoUstDoesNothing()
    {
        var items = Items(3);
        var w = new World(items, withUst: false, direct: true);
        foreach (uint id in items) w.Mgr.EnqueueItem(id);
        w.Run(5_000);
        Check.Eq(FakeHost.SalvageDirects.Count, 0, "no request without a UST");
        Check.Eq(FakeHost.SalvageExecutes.Count + FakeHost.SalvageAdds.Count, 0, "no panel either");
        Check.Eq(FakeHost.Chat.Count(c => c.Contains("no UST", StringComparison.Ordinal)), 1, "one chat warning");
        Check.Eq(w.Mgr.GetStateSnapshot().QueueCount, 3, "the items are kept");
        Check.False(w.Mgr.HasUst(), "HasUst false (looting leaves salvage-only items)");

        var ids = new List<uint>();
        World.AddUst(ids);
        _ = w.Cache[unchecked((int)Ust)];
        w.Run(15_000);
        Check.Eq(FakeHost.SalvageDirects.Count, 1, "a request once a UST is there");
        Check.True(items.All(id => !w.InWorld(id)), "every item salvaged");
    }

    private static void DirectGiveUpIsBounded()
    {
        uint stubborn = ItemBase + 7;
        var w = new World(new[] { stubborn }, direct: true);
        w.Unsalvageable.Add(stubborn); // the server never consumes it (no request, no panel)
        w.Mgr.EnqueueItem(stubborn);
        w.Run(10 * 60_000, step: 100);

        Check.Eq(FakeHost.SalvageDirects.Count, 3 * SalvageManager.MaxGiveUpRounds, "three rounds of three tries without the panel");
        Check.Eq(FakeHost.SalvageExecutes.Count, 3 * SalvageManager.MaxGiveUpRounds, "each tried once through the panel");
        Check.True(w.SalvageLogs.Any(l => l.Contains("Giving up on", StringComparison.Ordinal)), "giving up is logged");
        Check.Eq(w.Mgr.GetStateSnapshot().TrackedItems, 0, "no longer tracked");
        Check.True(w.Mgr.GetStateSnapshot().DirectOn, "the panel never succeeded either: no reason to turn it off");
    }

    private static void DirectCombineSweep()
    {
        uint bagA = 0x80000D01, bagB = 0x80000D02, bagC = 0x80000D03;
        var w = new World(Array.Empty<uint>(), direct: true);
        w.Settings.EnableCombineSalvage = true;
        w.AddBag(bagA, 20);
        w.AddBag(bagB, 30);
        w.AddBag(bagC, 40);
        w.Run(5_000);

        Check.Eq(FakeHost.SalvageDirects.Count, 1, "one request for the group");
        Check.Eq(string.Join(",", FakeHost.SalvageDirects[0].Ids.OrderBy(x => x)), string.Join(",", new[] { bagA, bagB, bagC }), "with the three bags");
        Check.Eq(PanelOpens, 0, "no panel");
        Check.True(!w.InWorld(bagA) && !w.InWorld(bagB) && !w.InWorld(bagC), "merged");
        Check.True(w.SalvageLogs.Any(l => l.Contains("Combine group 1/1 (without the panel): merged 3 bag(s)", StringComparison.Ordinal)), "the log says so");
    }

    private static void DirectCombineFallsBackToPanel()
    {
        uint bagA = 0x80000D01, bagB = 0x80000D02;
        var w = new World(Array.Empty<uint>(), direct: true) { ServerIgnoresDirect = true };
        w.Settings.EnableCombineSalvage = true;
        w.AddBag(bagA, 20);
        w.AddBag(bagB, 30);
        w.Run(8_000);

        Check.Eq(FakeHost.SalvageDirects.Count, 1, "one request for the group");
        Check.Eq(PanelOpens, 1, "then the panel, once");
        Check.Eq(FakeHost.SalvageExecutes.Count, 1, "one Salvage press");
        Check.True(!w.InWorld(bagA) && !w.InWorld(bagB), "the panel merged them");
        Check.True(w.SalvageLogs.Any(l => l.Contains("merged nothing", StringComparison.Ordinal) && l.Contains("trying the panel once", StringComparison.Ordinal)), "the fallback is logged");
        Check.True(w.SalvageLogs.Any(l => l.Contains("Combine group 1/1 (panel retry): merged", StringComparison.Ordinal)), "the retry's result is logged");
    }
}

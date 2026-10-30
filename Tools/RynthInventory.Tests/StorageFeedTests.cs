using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using RynthCore.Plugin.RynthInventory;
using RynthCore.Plugin.RynthInventory.Data;
using RynthCore.Plugin.Shared;

namespace RynthInventory.Tests
{
    // Storage another plugin reads for RynthInventory (Plugins/Shared/StorageFeed.cs): since 2026-10-20
    // Aelrynth's account vault comes from RynthAelrynth (which reads /mule-data) instead of RynthInventory
    // asking every server for it. RynthInventory only takes "storage" records and files them.
    internal static partial class Program
    {
        [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_storagePushes")]
        private static extern ref ConcurrentQueue<string> StoragePushes(RynthInventoryPlugin p);

        private static StorageFeedRecord Feed(uint id, string name, params StorageFeedItem[] items) => new(id, name, "Account vault", items);

        private static void StorageFeedTests()
        {
            var vaultItem = new StorageFeedItem(2147483649u, 100, "Acid Assagai", 100667000, 100667001, 100667002, 3, 100, 1);
            var coin = new StorageFeedItem(2147483650u, 101, "Pyreal", 100667003, 0, 0, 0, 0, 8192);

            // The payload round-trips and becomes an ordinary storage record.
            StorageFeedRecord? feed = StorageFeed.Parse(StorageFeed.ToJson(Feed(0xFF123456u, "Aelrynth vault", vaultItem, coin)), out string error);
            Check(feed != null && error == "", "a sender's payload parses: " + error);
            StorageRecord rec = ExternalStorage.ToRecord(feed!, Now);
            Eq(rec.Id, 0xFF123456u, "id kept");
            Eq(rec.Name, "Aelrynth vault", "name"); Eq(rec.Label, "Account vault", "label"); Eq(rec.Cell, 0u, "no cell (not a place in the world)");
            Eq(rec.SeenUtc, Now, "seen now");
            Eq(rec.Items.Count, 2, "items");
            ItemRecord a = rec.Items[0];
            Eq(a.Name, "Acid Assagai", "name"); Eq(a.Icon, 100667000u, "icon"); Eq(a.Underlay, 100667001u, "underlay");
            Eq(a.Overlay, 100667002u, "overlay"); Eq(a.Stack, 3, "stack"); Eq(a.MaxStack, 100, "max stack");
            Eq(a.Place, ItemPlace.Storage, "place");
            Eq(a.Category, ItemCategory.FromItemType(1), "category from the item type");
            Eq(rec.Items[1].Stack, 1, "a stack of 0 is a stack of 1");

            // Filed with the character's storage: the same id replaces, a new one adds.
            var storage = new List<StorageRecord> { new() { Id = 77, Name = "Chest", Label = "Chest", Items = { Item(1, "Sword") } } };
            List<StorageRecord> once = ExternalStorage.FileInto(storage, rec);
            Eq(once.Count, 2, "added beside the opened chest");
            StorageRecord newer = ExternalStorage.ToRecord(Feed(0xFF123456u, "Aelrynth vault", coin), Now.AddMinutes(10));
            List<StorageRecord> twice = ExternalStorage.FileInto(once, newer);
            Eq(twice.Count, 2, "the newer copy replaces the older");
            Eq(twice.Single(s => s.Id == 0xFF123456u).Items.Count, 1, "with its own contents");
            Eq(storage.Count, 1, "the old list is left alone (snapshots are never changed)");

            // Anything unusable is refused, never filed.
            Check(StorageFeed.Parse(null, out _) == null && StorageFeed.Parse("", out _) == null, "empty");
            Check(StorageFeed.Parse("{not json", out string bad) == null && bad.StartsWith("bad JSON", StringComparison.Ordinal), "broken JSON");
            Check(StorageFeed.Parse("{\"v\":1,\"id\":77,\"name\":\"Chest\",\"items\":[]}", out string real) == null && real.Contains("stand-in"),
                  "a real object's id can't be overwritten");
            Check(StorageFeed.Parse("{\"v\":2,\"id\":4278190081,\"name\":\"X\",\"items\":[]}", out _) == null, "a newer format is refused");
            Check(StorageFeed.Parse("{\"v\":1,\"id\":4278190081,\"name\":\" \",\"items\":[]}", out _) == null, "a store needs a name");
            string many = "{\"v\":1,\"id\":4278190081,\"name\":\"X\",\"items\":[" + string.Join(",", Enumerable.Repeat("{\"o\":1}", StorageFeed.MaxItems + 1)) + "]}";
            Check(StorageFeed.Parse(many, out _) == null, "too many items");

            // The receiver: only "storage" is taken, and only the newest few are kept until the tick files them.
            var plugin = new RynthInventoryPlugin();
            string json = StorageFeed.ToJson(Feed(0xFF000001u, "Aelrynth vault", coin));
            plugin.ApplyRemoteCommand("something-else", json);
            Eq(StoragePushes(plugin).Count, 0, "other commands are ignored");
            plugin.ApplyRemoteCommand("storage", "");
            Eq(StoragePushes(plugin).Count, 0, "an empty payload is ignored");
            for (int i = 0; i < 20; i++) plugin.ApplyRemoteCommand("storage", json);
            Check(StoragePushes(plugin).Count is > 0 and <= 8, $"queued, capped ({StoragePushes(plugin).Count})");

            // Nothing of any one server's store is left in RynthInventory itself.
            var asm = typeof(RynthInventoryPlugin).Assembly;
            Check(asm.GetType("RynthCore.Plugin.RynthInventory.Scanning.VaultFeed") == null, "no vault reader (/mule-data) in RynthInventory");
            Check(typeof(RynthInventoryPlugin).GetMethod(nameof(RynthInventoryPlugin.OnChatWindowText))!.DeclaringType != typeof(RynthInventoryPlugin),
                  "RynthInventory doesn't read chat (nothing to ask a server for, nothing to hide)");
        }
    }
}

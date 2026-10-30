using System;
using System.Collections.Generic;
using RynthCore.Plugin.Shared;

namespace RynthCore.Plugin.RynthInventory.Data;

/// <summary>
/// Storage another plugin read for us (Plugins/Shared/StorageFeed.cs): a server-side store the client
/// is never shown, e.g. Aelrynth's account vault from RynthAelrynth. Filed with the character's storage
/// like a container that was opened; RynthInventory itself knows nothing about any server's store.
/// Pure: RynthInventory.Tests covers it.
/// </summary>
internal static class ExternalStorage
{
    /// <summary>The feed's record as a storage record, seen now (<paramref name="seenUtc"/>).</summary>
    public static StorageRecord ToRecord(StorageFeedRecord feed, DateTime seenUtc)
    {
        var items = new List<ItemRecord>(feed.Items.Count);
        foreach (StorageFeedItem f in feed.Items)
        {
            var it = new ItemRecord
            {
                Id = f.Id,
                Wcid = f.Wcid,
                Name = f.Name,
                Icon = f.Icon,
                Underlay = f.Underlay,
                Overlay = f.Overlay,
                Stack = Math.Max(1, f.Stack),
                MaxStack = f.MaxStack,
                ItemType = f.ItemType,
                Place = ItemPlace.Storage,
            };
            it.Category = ItemCategory.FromItemType(it.ItemType);
            items.Add(it);
        }
        return new StorageRecord { Id = feed.Id, Name = feed.Name, Label = feed.Label, Cell = 0, SeenUtc = seenUtc, Items = items };
    }

    /// <summary>A copy of <paramref name="storage"/> with <paramref name="record"/> in it (replacing the one with its id).</summary>
    public static List<StorageRecord> FileInto(IReadOnlyList<StorageRecord> storage, StorageRecord record)
    {
        var list = new List<StorageRecord>(storage);
        int i = list.FindIndex(s => s.Id == record.Id);
        if (i >= 0) list[i] = record; else list.Add(record);
        return list;
    }
}

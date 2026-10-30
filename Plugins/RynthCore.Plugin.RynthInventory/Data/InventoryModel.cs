using System;
using System.Collections.Generic;

namespace RynthCore.Plugin.RynthInventory.Data;

// The saved data: one CharacterSnapshot per character per server, one file each.
// Dependency-free on purpose (no plugin SDK types): Tools\RynthInventory.Tests compiles the
// Data folder directly.

/// <summary>Where an item is: worn or wielded, carried (main pack or a side pack), or in storage.</summary>
internal static class ItemPlace
{
    public const string Equipped = "equipped";
    public const string Pack = "pack";
    public const string Storage = "storage";
}

/// <summary>One item, as read from the client without asking the server for anything.</summary>
internal sealed class ItemRecord
{
    public uint Id;
    public uint Wcid;
    public string Name = string.Empty;
    /// <summary>Icon, underlay and overlay texture ids (0x06xxxxxx; 0 = none).</summary>
    public uint Icon, Underlay, Overlay;
    public int Stack = 1;
    public int MaxStack;
    /// <summary>AC ItemType flags (STypeInt 1).</summary>
    public uint ItemType;
    /// <summary>A <see cref="ItemCategory"/> name.</summary>
    public string Category = ItemCategory.Misc;
    /// <summary>An <see cref="ItemPlace"/> value.</summary>
    public string Place = ItemPlace.Pack;
    public uint ContainerId;
    /// <summary>Display path inside the character, outermost first: "Main pack", "Sack" (a side pack) ...</summary>
    public string[] Path = Array.Empty<string>();
    /// <summary>Equip mask of the slot(s) worn in (CurrentWieldedLocation); 0 when not equipped.</summary>
    public uint Slot;
    /// <summary>MaterialType (STypeInt 131); 0 unknown or none.</summary>
    public int Material;
    /// <summary>ItemWorkmanship (STypeInt 105), known once the item was identified; 0 unknown.</summary>
    public int Workmanship;
    public int Value;
    public int Burden;
    /// <summary>ArmorLevel (STypeInt 28) once identified; 0 unknown.</summary>
    public int ArmorLevel;
    /// <summary>Uses left / most uses (Structure 92 / MaxStructure 91): kits, essences; 0 = none.</summary>
    public int Uses, MaxUses;
    /// <summary>EquipmentSetId (STypeInt 265) once identified; 0 none.</summary>
    public int SetId;
    public uint[] Spells = Array.Empty<uint>();
    /// <summary>Names for <see cref="Spells"/> when the scanner knew them (same order).</summary>
    public string[] SpellNames = Array.Empty<string>();
    /// <summary>The client had appraisal data for it when scanned (spells, workmanship, set are then known).</summary>
    public bool Identified;

    public string PathText => Path.Length == 0 ? string.Empty : string.Join(" > ", Path);
}

/// <summary>A container outside the character (house storage, a bank or vault) as last seen open.</summary>
internal sealed class StorageRecord
{
    public uint Id;
    public string Name = string.Empty;
    /// <summary>Where it is, for people: "Storage near 42.1N, 33.6E".</summary>
    public string Label = string.Empty;
    /// <summary>The landcell it was seen in (0 unknown).</summary>
    public uint Cell;
    public DateTime SeenUtc;
    public List<ItemRecord> Items = new();
}

/// <summary>Everything one character owned when last scanned, plus the storage it opened.</summary>
internal sealed class CharacterSnapshot
{
    public const int CurrentFormat = 1;
    public const string FormatTag = "rynthinventory";

    public int Format = CurrentFormat;
    public string Server = string.Empty;
    public string Account = string.Empty;
    public string Character = string.Empty;
    public uint CharacterId;
    /// <summary>When the carried inventory (Items) was last scanned.</summary>
    public DateTime ScannedUtc;
    public string PluginVersion = string.Empty;
    public List<ItemRecord> Items = new();
    public List<StorageRecord> Storage = new();

    // Not saved: where it was read from.
    public string FilePath = string.Empty;

    /// <summary>Server + character, the identity readers merge on (names are unique per server).</summary>
    public string Key => MakeKey(Server, Character);

    public static string MakeKey(string server, string character) =>
        (server ?? string.Empty).Trim().ToLowerInvariant() + "\u001f" + (character ?? string.Empty).Trim().ToLowerInvariant();

    /// <summary>The newest time anything in this snapshot was seen.</summary>
    public DateTime NewestUtc
    {
        get
        {
            DateTime t = ScannedUtc;
            foreach (StorageRecord s in Storage) if (s.SeenUtc > t) t = s.SeenUtc;
            return t;
        }
    }

    /// <summary>A hash over everything saved except the times, so an unchanged scan doesn't rewrite the file.</summary>
    public ulong ContentHash()
    {
        var h = new Fnv64();
        h.Add(Server); h.Add(Account); h.Add(Character); h.Add(CharacterId);
        h.Add((uint)Items.Count);
        foreach (ItemRecord i in Items) HashItem(ref h, i);
        h.Add((uint)Storage.Count);
        foreach (StorageRecord s in Storage)
        {
            h.Add(s.Id); h.Add(s.Name); h.Add(s.Label); h.Add(s.Cell); h.Add((uint)s.Items.Count);
            foreach (ItemRecord i in s.Items) HashItem(ref h, i);
        }
        return h.Value;
    }

    internal static void HashItem(ref Fnv64 h, ItemRecord i)
    {
        h.Add(i.Id); h.Add(i.Wcid); h.Add(i.Name); h.Add(i.Icon); h.Add(i.Underlay); h.Add(i.Overlay);
        h.Add((uint)i.Stack); h.Add((uint)i.MaxStack); h.Add(i.ItemType); h.Add(i.Category); h.Add(i.Place);
        h.Add(i.ContainerId); h.Add((uint)i.Path.Length);
        foreach (string p in i.Path) h.Add(p);
        h.Add(i.Slot); h.Add((uint)i.Material); h.Add((uint)i.Workmanship); h.Add((uint)i.Value); h.Add((uint)i.Burden);
        h.Add((uint)i.ArmorLevel); h.Add((uint)i.Uses); h.Add((uint)i.MaxUses); h.Add((uint)i.SetId);
        h.Add((uint)i.Spells.Length);
        foreach (uint s in i.Spells) h.Add(s);
        h.Add(i.Identified ? 1u : 0u);
    }
}

/// <summary>FNV-1a, 64 bit. Strings are hashed as UTF-16 code units with a terminator.</summary>
internal struct Fnv64
{
    private ulong _h;
    private bool _started;

    public ulong Value => _started ? _h : 14695981039346656037UL;

    public void Add(uint v)
    {
        if (!_started) { _h = 14695981039346656037UL; _started = true; }
        for (int i = 0; i < 4; i++)
        {
            _h ^= (byte)(v >> (i * 8));
            _h *= 1099511628211UL;
        }
    }

    public void Add(string? s)
    {
        if (s != null)
            foreach (char c in s) Add(c);
        Add(0xFFFFFFFFu);
    }
}

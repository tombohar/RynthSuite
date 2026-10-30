using System;
using System.Collections.Generic;
using System.Globalization;
using RynthCore.Plugin.RynthInventory.Data;
using RynthCore.PluginSdk;

namespace RynthCore.Plugin.RynthInventory.Scanning;

/// <summary>
/// Reads the character's inventory from the client through the plugin API. Plugin pump thread
/// only. Everything comes from what the client already has (the engine's object snapshot and
/// its property caches, filled by CreateObject and by the engine's own auto-identify); nothing
/// here sends anything to the server.
///
/// GetContainerContents(player) returns everything the player owns directly: the main pack's
/// items, the side packs, and the worn and wielded items (wielder = player). Each side pack's
/// contents come from GetContainerContents(pack).
/// </summary>
internal sealed class InventoryScanner
{
    // STypeInt
    private const uint StackSize = 12, MaxStackSize = 11, Value = 19, EncumbranceVal = 5, ItemsCapacity = 6,
        CurrentWieldedLocation = 10, MaterialType = 131, ItemWorkmanship = 105, ArmorLevel = 28,
        Structure = 92, MaxStructure = 91, EquipmentSetId = 265;
    // STypeDID
    private const uint DidIcon = 8, DidSpell = 28, DidIconOverlay = 50, DidIconUnderlay = 52;
    // STypeIID
    private const uint IidContainer = 2, IidWielder = 3, IidHouseOwner = 32;
    // ObjectDescriptionFlag
    private const uint BitfieldCorpse = 0x00002000, BitfieldVendor = 0x00000200, BitfieldPlayer = 0x00000008;

    private const int MaxContents = 1024;
    private const int MaxNesting = 3;

    private readonly uint[] _contents = new uint[MaxContents];
    private readonly uint[] _spellBuf = new uint[64];
    private readonly Dictionary<uint, bool> _isPack = new();

    public bool Warmed;

    public void Reset()
    {
        _isPack.Clear();
        Warmed = false;
    }

    /// <summary>
    /// The engine's object-table probes run on the first property reads; RynthAi touches them
    /// with a position and an int read before bulk scanning (WorldObjectCache.ScanFullInventory).
    /// </summary>
    public void WarmUp(RynthCoreHost host, uint player)
    {
        if (Warmed || player == 0) return;
        host.TryGetObjectPosition(player, out _, out _, out _, out _);
        host.TryGetObjectIntProperty(player, 1, out _);
        Warmed = true;
    }

    public static bool TryReadIdentity(RynthCoreHost host, out uint player, out string server, out string account, out string character)
    {
        player = host.HasGetPlayerId ? host.GetPlayerId() : 0;
        server = host.TryGetWorldName(out string w) ? w.Trim() : string.Empty;
        account = host.TryGetAccountName(out string a) ? a.Trim() : string.Empty;
        character = player != 0 && host.TryGetObjectName(player, out string n) ? n.Trim() : string.Empty;
        if (account.Length == 0) account = "unknown";
        return player != 0 && character.Length > 0 && server.Length > 0;
    }

    // ── Fingerprint ─────────────────────────────────────────────────────────

    /// <summary>A hash of what the character carries: ids, where each is, stack sizes. Cheap enough to run every poll.</summary>
    public ulong Fingerprint(RynthCoreHost host, uint player)
    {
        var h = new Fnv64();
        h.Add(player);
        FingerprintContainer(host, player, player, ref h, 0);
        return h.Value;
    }

    private void FingerprintContainer(RynthCoreHost host, uint player, uint container, ref Fnv64 h, int depth)
    {
        int n = host.GetContainerContents(container, _contents);
        if (n <= 0) { h.Add(0xDEADu); return; }
        uint[] ids = new uint[n];
        Array.Copy(_contents, ids, n);
        h.Add((uint)n);
        foreach (uint id in ids)
        {
            Ownership(host, id, out uint c, out uint w, out uint loc);
            host.TryGetObjectIntProperty(id, StackSize, out int stack);
            h.Add(id); h.Add(c); h.Add(w); h.Add(loc); h.Add((uint)stack);
            if (depth < MaxNesting && w == 0 && c == container && IsPack(host, id))
                FingerprintContainer(host, player, id, ref h, depth + 1);
        }
    }

    // ── Full scan ───────────────────────────────────────────────────────────

    /// <summary>Everything the character carries and wears.</summary>
    public List<ItemRecord> ScanCarried(RynthCoreHost host, uint player)
    {
        var items = new List<ItemRecord>();
        int n = host.GetContainerContents(player, _contents);
        if (n <= 0) return items;
        uint[] top = new uint[n];
        Array.Copy(_contents, top, n);

        var packs = new List<(uint Id, string Label)>();
        var packNames = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        string[] mainPath = { "Main pack" };
        foreach (uint id in top)
        {
            Ownership(host, id, out uint c, out uint w, out uint loc);
            if (w == player && w != 0)
            {
                if (loc == 0 && host.TryGetObjectIntProperty(id, CurrentWieldedLocation, out int l3)) loc = unchecked((uint)l3);
                ItemRecord? eq = Read(host, id, ItemPlace.Equipped, player, Array.Empty<string>(), loc);
                if (eq != null) items.Add(eq);
                continue;
            }
            if (c != player && c != 0) continue;   // moved away since the engine's snapshot
            ItemRecord? it = Read(host, id, ItemPlace.Pack, player, mainPath, 0);
            if (it == null) continue;
            items.Add(it);
            if (IsPack(host, id))
            {
                // Side packs are labelled by name ("Sack", "Sack 2" ...); the client's slot order isn't known here.
                packNames.TryGetValue(it.Name, out int seen);
                packNames[it.Name] = seen + 1;
                packs.Add((id, seen == 0 ? it.Name : it.Name + " " + (seen + 1).ToString(CultureInfo.InvariantCulture)));
            }
        }
        foreach (var (packId, label) in packs)
            ScanInto(host, packId, ItemPlace.Pack, new[] { "Main pack", label }, items, 1);
        return items;
    }

    /// <summary>A container outside the character (storage): its items and any packs inside it.</summary>
    public List<ItemRecord> ScanContainer(RynthCoreHost host, uint containerId)
    {
        var items = new List<ItemRecord>();
        ScanInto(host, containerId, ItemPlace.Storage, Array.Empty<string>(), items, 0);
        return items;
    }

    private void ScanInto(RynthCoreHost host, uint container, string place, string[] path, List<ItemRecord> items, int depth)
    {
        int n = host.GetContainerContents(container, _contents);
        if (n <= 0) return;
        uint[] ids = new uint[n];
        Array.Copy(_contents, ids, n);
        var packs = new List<(uint, string)>();
        var names = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (uint id in ids)
        {
            Ownership(host, id, out uint c, out uint w, out _);
            if (w != 0 || (c != container && c != 0)) continue;
            ItemRecord? it = Read(host, id, place, container, path, 0);
            if (it == null) continue;
            items.Add(it);
            if (depth < MaxNesting && IsPack(host, id))
            {
                names.TryGetValue(it.Name, out int seen);
                names[it.Name] = seen + 1;
                packs.Add((id, seen == 0 ? it.Name : it.Name + " " + (seen + 1).ToString(CultureInfo.InvariantCulture)));
            }
        }
        foreach (var (packId, label) in packs)
        {
            var sub = new string[path.Length + 1];
            Array.Copy(path, sub, path.Length);
            sub[path.Length] = label;
            ScanInto(host, packId, place, sub, items, depth + 1);
        }
    }

    private ItemRecord? Read(RynthCoreHost host, uint id, string place, uint container, string[] path, uint slot)
    {
        if (!host.TryGetObjectName(id, out string name) || string.IsNullOrWhiteSpace(name)) return null;
        var it = new ItemRecord
        {
            Id = id,
            Name = name.Trim(),
            Place = place,
            ContainerId = container,
            Path = path,
            Slot = slot,
        };
        if (host.TryGetItemType(id, out uint type)) it.ItemType = type;
        it.Category = ItemCategory.FromItemType(it.ItemType);
        if (host.HasGetObjectWcid && host.TryGetObjectWcid(id, out uint wcid)) it.Wcid = wcid;
        if (host.HasGetObjectDataIdProperty)
        {
            if (host.TryGetObjectDataIdProperty(id, DidIcon, out uint icon)) it.Icon = icon;
            if (host.TryGetObjectDataIdProperty(id, DidIconUnderlay, out uint under)) it.Underlay = under;
            if (host.TryGetObjectDataIdProperty(id, DidIconOverlay, out uint over)) it.Overlay = over;
        }
        if (host.TryGetObjectIntProperty(id, StackSize, out int stack) && stack > 0) it.Stack = stack;
        if (host.TryGetObjectIntProperty(id, MaxStackSize, out int maxStack)) it.MaxStack = Math.Max(0, maxStack);
        if (host.TryGetObjectIntProperty(id, Value, out int value)) it.Value = value;
        if (host.TryGetObjectIntProperty(id, EncumbranceVal, out int burden)) it.Burden = burden;
        if (host.TryGetObjectIntProperty(id, MaterialType, out int material)) it.Material = Math.Max(0, material);
        if (host.TryGetObjectIntProperty(id, ItemWorkmanship, out int work)) it.Workmanship = Math.Max(0, work);
        if (host.TryGetObjectIntProperty(id, ArmorLevel, out int al)) it.ArmorLevel = Math.Max(0, al);
        if (host.TryGetObjectIntProperty(id, MaxStructure, out int maxUses) && maxUses > 0)
        {
            it.MaxUses = maxUses;
            if (host.TryGetObjectIntProperty(id, Structure, out int uses)) it.Uses = Math.Max(0, uses);
        }
        if (host.TryGetObjectIntProperty(id, EquipmentSetId, out int set)) it.SetId = Math.Max(0, set);
        it.Identified = host.HasHasAppraisalData && host.HasAppraisalData(id);

        if (host.HasGetObjectSpellIds)
        {
            int count = host.GetObjectSpellIds(id, _spellBuf, _spellBuf.Length);
            if (count > 0)
            {
                count = Math.Min(count, _spellBuf.Length);
                it.Spells = new uint[count];
                Array.Copy(_spellBuf, it.Spells, count);
            }
        }
        // A scroll, gem or potion that casts one spell carries it in its PublicWeenieDesc.
        if (it.Spells.Length == 0 && host.HasGetObjectDataIdProperty
            && host.TryGetObjectDataIdProperty(id, DidSpell, out uint spell) && spell != 0 && spell < 0x10000)
            it.Spells = new[] { spell };
        if (it.Spells.Length > 0)
        {
            it.SpellNames = new string[it.Spells.Length];
            for (int i = 0; i < it.Spells.Length; i++) it.SpellNames[i] = SpellTable.Name(it.Spells[i]);
        }
        return it;
    }

    // ── Storage ─────────────────────────────────────────────────────────────

    /// <summary>Whether an opened container counts as storage worth remembering.</summary>
    public static bool IsStorage(RynthCoreHost host, uint id, uint player, string[] words, bool kept, out string name, out string why)
    {
        why = string.Empty;
        name = host.TryGetObjectName(id, out string n) ? n.Trim() : string.Empty;
        if (id == 0 || id == player || name.Length == 0) { why = "no name"; return false; }
        if (host.TryGetObjectBitfield(id, out uint bf) && (bf & (BitfieldCorpse | BitfieldVendor | BitfieldPlayer)) != 0)
        {
            why = "a corpse or vendor";
            return false;
        }
        if (name.StartsWith("Corpse of ", StringComparison.OrdinalIgnoreCase)) { why = "a corpse"; return false; }
        if (IsOwnedBy(host, id, player)) { why = "carried"; return false; }
        if (kept) { why = "kept with /ginv keep"; return true; }
        if (host.HasGetObjectInstanceIdProperty && host.TryGetObjectInstanceIdProperty(id, IidHouseOwner, out uint houseOwner) && houseOwner != 0)
        {
            why = "house storage";
            return true;
        }
        string lower = name.ToLowerInvariant();
        foreach (string word in words)
        {
            if (word.Length > 0 && lower.Contains(word, StringComparison.Ordinal))
            {
                why = $"name has \"{word}\"";
                return true;
            }
        }
        why = "not storage (use /ginv keep while it is open to record it anyway)";
        return false;
    }

    /// <summary>"Storage near 42.1N, 33.6E" (the player's position when it opened), or the cell when there are no coordinates.</summary>
    public static string StorageLabel(RynthCoreHost host, uint id, string name, out uint cell)
    {
        cell = 0;
        if (host.TryGetObjectPosition(id, out uint c, out _, out _, out _)) cell = c;
        string baseName = name.Length > 0 ? name : "Storage";
        if (host.HasGetCurCoords && host.TryGetCurCoords(out double ns, out double ew) && !(ns == 0 && ew == 0))
            return $"{baseName} near {Coord(ns, 'N', 'S')}, {Coord(ew, 'E', 'W')}";
        return cell != 0 ? $"{baseName} (cell 0x{cell:X8})" : baseName;
    }

    private static string Coord(double v, char pos, char neg) =>
        Math.Abs(v).ToString("0.0", CultureInfo.InvariantCulture) + (v >= 0 ? pos : neg);

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static bool IsOwnedBy(RynthCoreHost host, uint id, uint player)
    {
        uint cur = id;
        for (int i = 0; i < 4 && cur != 0; i++)
        {
            Ownership(host, cur, out uint c, out uint w, out _);
            if (c == player || w == player) return true;
            cur = c;
        }
        return false;
    }

    private static void Ownership(RynthCoreHost host, uint id, out uint container, out uint wielder, out uint location)
    {
        container = wielder = location = 0;
        if (host.HasGetObjectOwnershipInfo && host.TryGetObjectOwnershipInfo(id, out container, out wielder, out location))
            return;
        if (host.HasGetObjectInstanceIdProperty)
        {
            host.TryGetObjectInstanceIdProperty(id, IidContainer, out container);
            host.TryGetObjectInstanceIdProperty(id, IidWielder, out wielder);
        }
        if (host.HasGetObjectWielderInfo && host.TryGetObjectWielderInfo(id, out uint w2, out uint l2))
        {
            if (wielder == 0) wielder = w2;
            location = l2;
        }
    }

    /// <summary>A side pack (or a pack inside storage): a container with item slots. Cached; it never changes.</summary>
    private bool IsPack(RynthCoreHost host, uint id)
    {
        if (_isPack.TryGetValue(id, out bool known)) return known;
        if (!host.TryGetItemType(id, out uint type) || type == 0) return false;   // not known yet: ask again later
        bool pack = (type & ItemCategory.ContainerFlag) != 0
                    && host.TryGetObjectIntProperty(id, ItemsCapacity, out int cap) && cap > 0;
        _isPack[id] = pack;
        return pack;
    }
}

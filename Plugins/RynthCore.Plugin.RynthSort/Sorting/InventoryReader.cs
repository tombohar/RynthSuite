using System;
using System.Collections.Generic;
using RynthCore.PluginSdk;

namespace RynthCore.Plugin.RynthSort.Sorting;

/// <summary>
/// Reads the character's packs from the client into a <see cref="SortInventory"/>. Plugin pump
/// thread only. GetContainerContents(player) gives the main pack's items, the side packs and the
/// worn and wielded items; each side pack's items come from GetContainerContents(pack). A side
/// pack is a container that holds items (ItemsCapacity above 0); foci hold none and are skipped,
/// as are worn and wielded items. Names, classes and stack limits are kept per item id once read.
/// GetContainerContents sorts by object id; with the engine's slot orders (API v80
/// GetContainerOrder: what the server sent at login and with every move) each pack's items are put
/// in their real slot order and the side packs in the pack column's order, top first, whenever
/// that order names exactly what was read (<see cref="SortPack.ApplyOrder"/>,
/// <see cref="SortInventory.ApplyPackOrder"/>). Without it the id order stays and nothing is
/// reordered inside packs.
/// </summary>
internal sealed class InventoryReader
{
    private const uint IntItemsCapacity = 6, IntMaxStackSize = 11, IntStackSize = 12;

    private const uint ItemTypeMeleeWeapon = 0x00000001, ItemTypeArmor = 0x00000002, ItemTypeClothing = 0x00000004,
        ItemTypeJewelry = 0x00000008, ItemTypeCreature = 0x00000010, ItemTypeFood = 0x00000020, ItemTypeMoney = 0x00000040,
        ItemTypeMisc = 0x00000080, ItemTypeMissileWeapon = 0x00000100, ItemTypeContainer = 0x00000200,
        ItemTypeGem = 0x00000800, ItemTypeSpellComponents = 0x00001000, ItemTypeWritable = 0x00002000,
        ItemTypeKey = 0x00004000, ItemTypeCaster = 0x00008000, ItemTypePortal = 0x00010000,
        ItemTypePromissoryNote = 0x00040000, ItemTypeManaStone = 0x00080000, ItemTypeService = 0x00100000,
        ItemTypeCraftCookingBase = 0x00400000, ItemTypeCraftAlchemyBase = 0x00800000,
        ItemTypeCraftFletchingBase = 0x02000000, ItemTypeCraftAlchemyIntermediate = 0x04000000,
        ItemTypeCraftFletchingIntermediate = 0x08000000, ItemTypeLifeStone = 0x10000000,
        ItemTypeTinkeringTool = 0x20000000, ItemTypeTinkeringMaterial = 0x40000000;

    // PublicWeenieDesc bitfield bits RynthLua's classifier reads for items.
    private const uint BfFood = 0x8000, BfHealer = 0x10000, BfLockpick = 0x20000;

    private sealed class Known
    {
        public string Name = string.Empty;
        public int Class;
        public uint Wcid;
        public int MaxStack = 1;
    }

    private readonly Dictionary<uint, Known> _known = new();
    private uint[] _buf = new uint[512];
    private uint[] _order = new uint[128];

    public void Reset() => _known.Clear();

    /// <summary>The packs now, or null when the character or its inventory isn't readable yet.</summary>
    public SortInventory? Read(RynthCoreHost host)
    {
        if (!host.HasGetPlayerId || !host.HasGetContainerContents) return null;
        uint player = host.GetPlayerId();
        if (player == 0) return null;

        var inv = new SortInventory();
        inv.Main.Id = player;
        inv.Main.Name = "Main pack";
        inv.Main.Capacity = host.TryGetObjectIntProperty(player, IntItemsCapacity, out int mainCap) && mainCap > 0 ? mainCap : 0;

        uint[] top = Contents(host, player);
        if (top.Length == 0) return null;   // not loaded yet: never plan from an empty read
        foreach (uint id in top)
        {
            if (id == 0) continue;
            uint type = host.TryGetItemType(id, out uint t) ? t : 0;
            bool isContainer = (type & ItemTypeContainer) != 0;
            int cap = 0;
            bool capRead = isContainer && host.TryGetObjectIntProperty(id, IntItemsCapacity, out cap);
            // Every container entry, foci too and before the wielded check: the Lua script's count.
            if (isContainer) inv.Slots.Add(new SortSlot(id, capRead ? Math.Max(cap, 0) : -1));
            if (Wielded(host, id)) continue;
            if (isContainer)
            {
                // A side pack holds items; foci (containers that hold none) aren't packs or items.
                if (capRead && cap > 0)
                {
                    var pack = new SortPack { Id = id, Capacity = cap, Name = host.TryGetObjectName(id, out string pn) ? pn : string.Empty };
                    foreach (uint inner in Contents(host, id))
                    {
                        if (inner == 0) continue;
                        uint it = host.TryGetItemType(inner, out uint itt) ? itt : 0;
                        if ((it & ItemTypeContainer) != 0) continue;
                        pack.Items.Add(Item(host, inner, it));
                    }
                    inv.Packs.Add(pack);
                }
                continue;
            }
            inv.Main.Items.Add(Item(host, id, type));
        }
        if (host.HasContainerOrder)
        {
            if (host.TryGetContainerOrder(player, false, ref _order, out int n)) inv.Main.ApplyOrder(_order, n);
            foreach (SortPack p in inv.Packs)
                if (host.TryGetContainerOrder(p.Id, false, ref _order, out n)) p.ApplyOrder(_order, n);
            if (host.TryGetContainerOrder(player, true, ref _order, out n)) inv.ApplyPackOrder(_order, n);
        }
        return inv;
    }

    /// <summary>The slot <paramref name="itemId"/> holds in <paramref name="container"/> (the engine's slot order), or -1.</summary>
    public int SlotOf(RynthCoreHost host, uint container, uint itemId)
    {
        if (!host.HasContainerOrder || !host.TryGetContainerOrder(container, false, ref _order, out int n)) return -1;
        return Array.IndexOf(_order, itemId, 0, n);
    }

    private uint[] Contents(RynthCoreHost host, uint container)
    {
        int n = host.GetContainerContents(container, _buf);
        if (n >= _buf.Length)
        {
            _buf = new uint[_buf.Length * 2];
            n = host.GetContainerContents(container, _buf);
        }
        if (n <= 0) return Array.Empty<uint>();
        var ids = new uint[n];
        Array.Copy(_buf, ids, n);
        return ids;
    }

    private static bool Wielded(RynthCoreHost host, uint id)
    {
        if (host.HasGetObjectOwnershipInfo && host.TryGetObjectOwnershipInfo(id, out _, out uint wielder, out _))
            return wielder != 0;
        return host.HasGetObjectWielderInfo && host.TryGetObjectWielderInfo(id, out uint w, out _) && w != 0;
    }

    private SortItem Item(RynthCoreHost host, uint id, uint type)
    {
        if (!_known.TryGetValue(id, out Known? k))
        {
            k = new Known();
            k.Name = host.TryGetObjectName(id, out string name) ? name : string.Empty;
            k.Wcid = host.HasGetObjectWcid && host.TryGetObjectWcid(id, out uint wcid) ? wcid : 0;
            k.MaxStack = host.TryGetObjectIntProperty(id, IntMaxStackSize, out int max) && max > 0 ? max : 1;
            k.Class = Classify(host, id, type, k.Name, out bool certain);
            // Kept once the client had the data (a fresh object can lack its name or type for a tick).
            if (certain && k.Name.Length > 0) _known[id] = k;
        }
        int stack = host.TryGetObjectIntProperty(id, IntStackSize, out int s) && s > 0 ? s : 1;
        return new SortItem { Id = id, Name = k.Name, ObjectClass = k.Class, Wcid = k.Wcid, Stack = stack, MaxStack = k.MaxStack };
    }

    /// <summary>
    /// The item's ObjectClass the way RynthLua's game.World classifies items (GameWorldObject.cs:
    /// the healer / lockpick / food bits, then the ItemType flags most specific first, scrolls and
    /// foci by name), so a rule means the same in both.
    /// </summary>
    internal static int Classify(RynthCoreHost host, uint id, uint type, string name, out bool certain)
    {
        certain = false;
        uint bf = 0;
        bool gotBf = host.HasGetObjectBitfield && host.TryGetObjectBitfield(id, out bf) && bf != 0;
        if (!gotBf) bf = 0;
        return ClassifyFrom(type, bf, name, out certain);
    }

    /// <summary>The classification from the raw data (pure).</summary>
    internal static int ClassifyFrom(uint type, uint bitfield, string name, out bool certain)
    {
        certain = false;
        name ??= string.Empty;
        if (type != 0 && (type & ItemTypeCreature) != 0) { certain = true; return ObjectClasses.Monster; }
        if (bitfield != 0)
        {
            if ((bitfield & BfHealer) != 0) { certain = true; return ObjectClasses.HealingKit; }
            if ((bitfield & BfLockpick) != 0) { certain = true; return ObjectClasses.Lockpick; }
        }
        if (type != 0)
        {
            certain = true;
            int cls = ClassifyByItemType(type);
            if (cls == ObjectClasses.Book && name.Contains("Scroll", StringComparison.OrdinalIgnoreCase)) return ObjectClasses.Scroll;
            if (name.StartsWith("Foci of", StringComparison.OrdinalIgnoreCase)) return ObjectClasses.Foci;
            if (cls == ObjectClasses.Misc && (bitfield & BfFood) != 0) return ObjectClasses.Food;
            return cls;
        }
        if ((bitfield & BfFood) != 0) { certain = true; return ObjectClasses.Food; }
        return ObjectClasses.Unknown;
    }

    private static int ClassifyByItemType(uint t)
    {
        if ((t & ItemTypePromissoryNote) != 0) return ObjectClasses.TradeNote;
        if ((t & ItemTypeManaStone) != 0) return ObjectClasses.ManaStone;
        if ((t & ItemTypeSpellComponents) != 0) return ObjectClasses.SpellComponent;
        if ((t & ItemTypeGem) != 0) return ObjectClasses.Gem;
        if ((t & ItemTypeKey) != 0) return ObjectClasses.Key;
        if ((t & ItemTypeLifeStone) != 0) return ObjectClasses.Lifestone;
        if ((t & ItemTypeTinkeringMaterial) != 0) return ObjectClasses.Salvage;
        if ((t & ItemTypeTinkeringTool) != 0) return ObjectClasses.Ust;
        if ((t & ItemTypeCraftCookingBase) != 0) return ObjectClasses.BaseCooking;
        if ((t & ItemTypeCraftAlchemyBase) != 0) return ObjectClasses.BaseAlchemy;
        if ((t & ItemTypeCraftFletchingBase) != 0) return ObjectClasses.BaseFletching;
        if ((t & ItemTypeCraftAlchemyIntermediate) != 0) return ObjectClasses.CraftedAlchemy;
        if ((t & ItemTypeCraftFletchingIntermediate) != 0) return ObjectClasses.CraftedFletching;
        if ((t & ItemTypeService) != 0) return ObjectClasses.Services;
        if ((t & ItemTypePortal) != 0) return ObjectClasses.Portal;
        if ((t & ItemTypeWritable) != 0) return ObjectClasses.Book;
        if ((t & ItemTypeContainer) != 0) return ObjectClasses.Container;
        if ((t & ItemTypeCaster) != 0) return ObjectClasses.WandStaffOrb;
        if ((t & ItemTypeMissileWeapon) != 0) return ObjectClasses.MissileWeapon;
        if ((t & ItemTypeArmor) != 0) return ObjectClasses.Armor;
        if ((t & ItemTypeMeleeWeapon) != 0) return ObjectClasses.MeleeWeapon;
        if ((t & ItemTypeClothing) != 0) return ObjectClasses.Clothing;
        if ((t & ItemTypeJewelry) != 0) return ObjectClasses.Jewelry;
        if ((t & ItemTypeFood) != 0) return ObjectClasses.Food;
        if ((t & ItemTypeMoney) != 0) return ObjectClasses.Money;
        if ((t & ItemTypeMisc) != 0) return ObjectClasses.Misc;
        return ObjectClasses.Unknown;
    }
}

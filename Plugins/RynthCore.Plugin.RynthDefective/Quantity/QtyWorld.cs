// ============================================================================
//  RynthDefective - Quantity/QtyWorld.cs
//  What the quantity box (QuantityController) needs from the game, as an
//  interface so the tests drive it offline (RynthDefective.Tests), and the
//  engine host's side of it. Plugin pump thread only.
//
//  Every call is behind its Has* check: splitting (SplitStackInternal, as old
//  as the plugin API), giving with a count (GiveObjectTo, v62), vendor trading
//  (v67), the server's refusals (v70), the pack column's order (v80) and the
//  active server's list (v86, for its plugin's bank values) are each used
//  only where the engine has them. Nothing here needs more than the live
//  release's API 92, and the Selection window itself needs v90.
// ============================================================================

using System;
using System.Collections.Generic;
using RynthCore.Plugin.RynthDefective.Model;
using RynthCore.Plugin.Shared;
using RynthCore.PluginSdk;

namespace RynthCore.Plugin.RynthDefective.Quantity;

internal enum QtyPackKind { Main, Side, Open }

/// <summary>A place a split can go: the main pack, a side pack, or the container you have open.</summary>
internal readonly record struct QtyPack(uint Id, string Name, int Capacity, int Count, QtyPackKind Kind)
{
    /// <summary>Free item slots; -1 = unknown (the server decides).</summary>
    public int Free => Capacity > 0 && Count >= 0 ? Math.Max(0, Capacity - Count) : -1;

    /// <summary>In a sentence: "the main pack", "Sack", "Chest".</summary>
    public string Where => Kind == QtyPackKind.Main ? "the main pack" : Name;

    /// <summary>On a button: "Main pack", "Sack", "Chest (open)".</summary>
    public string Label => Kind switch
    {
        QtyPackKind.Main => "Main pack",
        QtyPackKind.Open => Name + " (open)",
        _ => Name,
    };
}

internal interface IQtyWorld
{
    long NowMs { get; }
    uint PlayerId { get; }
    uint SelectedId { get; }
    /// <summary>AC's selection before this one (0 = none or unknown).</summary>
    uint PreviousSelectedId { get; }
    /// <summary>The chest or corpse you have open (0 = none).</summary>
    uint OpenContainerId { get; }
    bool TryGetName(uint id, out string name);
    /// <summary>The client knows the object.</summary>
    bool Exists(uint id);
    /// <summary>Someone to give to: a player or an NPC (a creature that isn't a monster).</summary>
    bool CanReceive(uint id);
    /// <summary>Where it is: its container and its wielder (0 = none). False = the client doesn't know it.</summary>
    bool TryGetOwner(uint id, out uint container, out uint wielder);
    /// <summary>Its stack size; 0 = unknown.</summary>
    int StackOf(uint id);
    /// <summary>The main pack first, then the side packs (the pack column's order), then the open container.</summary>
    IReadOnlyList<QtyPack> Packs();

    bool CanSplit { get; }
    /// <summary>Splits <paramref name="n"/> off the stack into <paramref name="into"/> (a new stack there).</summary>
    bool Split(uint id, uint into, int n);
    bool CanGive { get; }
    /// <summary>Gives <paramref name="n"/> of it (0 = the whole of it) to a player or NPC.</summary>
    bool Give(uint id, uint to, int n);

    /// <summary>Counts the server's refusals (0 when the engine can't say): note it before a send, compare after.</summary>
    int RefusalSeq { get; }
    bool LastRefusal(out uint error, out uint eventType, out uint objectId);

    bool CanTrade { get; }
    /// <summary>The vendor open now (false = none).</summary>
    bool TryGetVendor(out VendorInfo info);
    IReadOnlyList<VendorItem> VendorItems();
    /// <summary>Asks the engine to buy; the request id, or 0 when it refused at once (the trade status says why).</summary>
    uint Buy(uint vendorId, uint itemId, int n);
    bool TryGetTradeStatus(out VendorTradeStatus status);
    /// <summary>Free container slots (for packs and foci bought); -1 = unknown.</summary>
    int FreeContainerSlots();
    /// <summary>Burden you can still carry (three times your capacity, as ACE); -1 = unknown.</summary>
    long FreeBurden();
    /// <summary>The active server's bank, as its plugin says (VendorBank.None: none, or nobody says).</summary>
    VendorBank Bank();
}

/// <summary>The engine host's side (RynthCoreHost). Plugin pump thread only.</summary>
internal sealed class HostQtyWorld : IQtyWorld
{
    // STypeInt
    private const uint EncumbranceVal = 5, ItemsCapacity = 6, ContainersCapacity = 7, StackSize = 12, EncumbranceCapacity = 96;
    private const uint ItemTypeCreature = 0x10, ItemTypeContainer = 0x200;
    // PublicWeenieDesc bitfield (ObjectDescriptionFlag).
    private const uint BitPlayer = 0x8, BitAttackable = 0x10;
    private const int MainPackDefault = 102, ContainerSlotsDefault = 7;

    private readonly RynthCoreHost _h;
    private uint[] _contents = new uint[256];
    private uint[] _order = new uint[16];
    private uint _vendorId, _vendorGeneration;
    private VendorItem[] _vendorItems = Array.Empty<VendorItem>();
    // The active server and its plugin's profile (v86 + "RynthServer.Profile"), for its bank values.
    private readonly ServerStateClient _server = new();
    private int _bankRevision = -1;
    private VendorBank _bank = VendorBank.None;

    public HostQtyWorld(RynthCoreHost host) => _h = host;

    /// <summary>Login or logout: the next server may be another (its bank read afresh).</summary>
    public void Reset()
    {
        _server.Clear();
        _bankRevision = -1;
        _bank = VendorBank.None;
    }

    public long NowMs => Environment.TickCount64;
    public uint PlayerId => _h.HasGetPlayerId ? _h.GetPlayerId() : 0;
    public uint SelectedId => _h.HasGetSelectedItemId ? _h.GetSelectedItemId() : 0;
    public uint PreviousSelectedId => _h.HasGetPreviousSelectedItemId ? _h.GetPreviousSelectedItemId() : 0;
    public uint OpenContainerId => _h.HasGetGroundContainerId ? _h.GetGroundContainerId() : 0;

    public bool TryGetName(uint id, out string name)
    {
        name = string.Empty;
        if (id == 0 || !_h.TryGetObjectName(id, out string n) || string.IsNullOrWhiteSpace(n)) return false;
        name = n.Trim();
        return true;
    }

    public bool Exists(uint id) => id != 0 && _h.TryGetItemType(id, out _);

    /// <summary>
    /// A creature that is a player (description bit Player 0x8) or isn't attackable (Attackable 0x10: a
    /// monster): a monster selected in a fight never becomes the give target. Without the description
    /// bits (an older engine) any creature; the server refuses a give it doesn't take.
    /// </summary>
    public bool CanReceive(uint id)
    {
        if (id == 0 || !_h.TryGetItemType(id, out uint t) || (t & ItemTypeCreature) == 0) return false;
        if (!_h.HasGetObjectBitfield || !_h.TryGetObjectBitfield(id, out uint bits)) return true;
        return (bits & BitPlayer) != 0 || (bits & BitAttackable) == 0;
    }

    public bool TryGetOwner(uint id, out uint container, out uint wielder)
    {
        container = wielder = 0;
        if (id == 0) return false;
        if (_h.HasGetObjectOwnershipInfo && _h.TryGetObjectOwnershipInfo(id, out container, out wielder, out _)) return true;
        return false;
    }

    public int StackOf(uint id) => id != 0 && _h.TryGetObjectIntProperty(id, StackSize, out int s) && s > 0 ? s : 0;

    private int Int(uint id, uint stype) => _h.TryGetObjectIntProperty(id, stype, out int v) ? v : -1;

    public IReadOnlyList<QtyPack> Packs()
    {
        var packs = new List<QtyPack>(8);
        uint player = PlayerId;
        if (player == 0) return packs;
        int mainCap = Int(player, ItemsCapacity);
        packs.Add(new QtyPack(player, "Main pack", mainCap > 0 ? mainCap : MainPackDefault, Count(player), QtyPackKind.Main));

        // The side packs, in the pack column's order when the engine knows it (v80), else as the client lists them.
        var ids = new List<uint>(8);
        if (_h.HasContainerOrder && _h.TryGetContainerOrder(player, true, ref _order, out int ordered) && ordered > 0)
            for (int i = 0; i < ordered && i < _order.Length; i++) ids.Add(_order[i]);
        else if (_h.HasGetContainerContents)
        {
            int n = _h.GetContainerContents(player, _contents);
            if (n > _contents.Length && n <= 4096) { _contents = new uint[n]; n = _h.GetContainerContents(player, _contents); }
            for (int i = 0; i < n && i < _contents.Length; i++) ids.Add(_contents[i]);
        }
        var names = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (uint id in ids)
        {
            if (id == 0 || !_h.TryGetItemType(id, out uint type) || (type & ItemTypeContainer) == 0) continue;
            int cap = Int(id, ItemsCapacity);
            if (cap <= 0) continue;   // a focus holds no items
            if (TryGetOwner(id, out uint c, out uint w) && (c != player || w != 0)) continue;
            string name = TryGetName(id, out string nm) ? nm : "Pack";
            names[name] = names.TryGetValue(name, out int seen) ? seen + 1 : 1;
            if (names[name] > 1) name += " " + names[name];
            packs.Add(new QtyPack(id, name, cap, Count(id), QtyPackKind.Side));
        }

        uint open = OpenContainerId;
        if (open != 0 && open != player && !packs.Exists(p => p.Id == open))
        {
            string name = TryGetName(open, out string nm) ? nm : "The open container";
            packs.Add(new QtyPack(open, name, Int(open, ItemsCapacity), Count(open), QtyPackKind.Open));
        }
        return packs;
    }

    private int Count(uint container) => _h.HasGetNumContainedItems ? _h.GetNumContainedItems(container) : -1;

    public bool CanSplit => _h.HasSplitStackInternal;
    // Slot 0 with a count below the stack: the client's StackableSplitToContainer, as the engine's own
    // Inventory panel splits (InventoryModel). The new stack takes a free slot of the target.
    public bool Split(uint id, uint into, int n) => CanSplit && n >= 1 && _h.SplitStackInternal(id, into, 0, n);
    public bool CanGive => _h.HasGiveObjectTo;
    public bool Give(uint id, uint to, int n) => CanGive && _h.GiveObjectTo(id, to, Math.Max(0, n));

    public int RefusalSeq => _h.HasGetLastWeenieError && _h.TryGetLastWeenieError(out int seq, out _, out _, out _) ? seq : 0;

    public bool LastRefusal(out uint error, out uint eventType, out uint objectId)
    {
        error = eventType = objectId = 0;
        return _h.HasGetLastWeenieError && _h.TryGetLastWeenieError(out _, out error, out eventType, out objectId);
    }

    public bool CanTrade => _h.HasVendorTrade;

    public bool TryGetVendor(out VendorInfo info)
    {
        info = null!;
        return CanTrade && _h.TryGetVendorInfo(out info) && info.VendorId != 0;
    }

    /// <summary>The open vendor's list, read again only when the vendor or its list (Generation) changed.</summary>
    public IReadOnlyList<VendorItem> VendorItems()
    {
        if (!TryGetVendor(out VendorInfo info))
        {
            _vendorId = 0;
            _vendorItems = Array.Empty<VendorItem>();
            return _vendorItems;
        }
        if (info.VendorId != _vendorId || info.Generation != _vendorGeneration || _vendorItems.Length == 0)
        {
            _vendorItems = _h.GetVendorItems();
            _vendorId = info.VendorId;
            _vendorGeneration = info.Generation;
        }
        return _vendorItems;
    }

    public uint Buy(uint vendorId, uint itemId, int n) => CanTrade ? _h.VendorBuy(itemId, n, vendorId) : 0;

    public bool TryGetTradeStatus(out VendorTradeStatus status)
    {
        status = null!;
        return CanTrade && _h.TryGetVendorTradeStatus(out status);
    }

    public int FreeContainerSlots()
    {
        uint player = PlayerId;
        if (player == 0 || !_h.HasGetNumContainedContainers) return -1;
        int n = _h.GetNumContainedContainers(player);
        if (n < 0) return -1;
        int cap = Int(player, ContainersCapacity);
        return Math.Max(0, (cap > 0 ? cap : ContainerSlotsDefault) - n);
    }

    public long FreeBurden()
    {
        uint player = PlayerId;
        if (player == 0) return -1;
        int carried = Int(player, EncumbranceVal), cap = Int(player, EncumbranceCapacity);
        if (carried < 0 || cap <= 0) return -1;   // unknown: the engine checks with its own reading
        return Math.Max(0, 3L * cap - carried);
    }

    /// <summary>
    /// The active server's plugin's "vendorBank" / "vendorBankPyreals" values (polled every 2 s; read
    /// again only when the server or its profile changed). Before v86, or with no server plugin: none.
    /// </summary>
    public VendorBank Bank()
    {
        if (!_h.HasGetServerStateJson) return VendorBank.None;
        _server.Poll(_h, NowMs);
        if (_server.Revision != _bankRevision)
        {
            _bankRevision = _server.Revision;
            _bank = VendorBank.Read(_server.ProfileJson);
        }
        return _bank;
    }
}

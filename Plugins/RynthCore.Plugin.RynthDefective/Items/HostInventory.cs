using System;
using System.Collections.Generic;
using RynthCore.PluginSdk;

namespace RynthCore.Plugin.RynthDefective.Items;

/// <summary>
/// Reads what the character carries and wears from the client into <see cref="CarriedItem"/>s.
/// Plugin pump thread only. Everything comes from what the client already has (the engine's
/// object snapshot and its property caches); nothing is sent to the server.
/// GetContainerContents(player) returns the main pack's items, the side packs and the worn and
/// wielded items; each side pack's items come from GetContainerContents(pack). What an item IS
/// (name, wcid, material, kind, picture) is kept per object id once read; what changes (stack,
/// uses left, whether it is in hand) is read every time.
/// </summary>
internal sealed class HostInventory
{
    // STypeInt
    private const uint StackSize = 12, MaxStackSize = 11, ValidLocations = 9, CurrentWieldedLocation = 10, UiEffects = 18,
        DamageType = 45, CombatUse = 51, MaterialType = 131, Structure = 92, MaxStructure = 91;
    // STypeDID
    private const uint DidIcon = 8, DidIconOverlay = 50, DidIconUnderlay = 52;

    private const int MaxContents = 1024;
    private const int MaxNesting = 3;
    private const uint ItemTypeContainer = 0x200;
    /// <summary>A caster whose damage type has not arrived (it comes with an identify) is read again after this.</summary>
    private const long RecheckMs = 15_000;

    private sealed class Static
    {
        public string Name = string.Empty;
        public uint Wcid;
        public int Material;
        public uint ItemType, Bitfield;
        public int MaxStack = 1, MaxUses, ValidLocations, CombatUse, DamageType, UiEffects;
        public uint Icon, Underlay, Overlay;
        public bool IsContainer;
        public long ReadMs;
    }

    private readonly Dictionary<uint, Static> _static = new();
    private readonly uint[] _contents = new uint[MaxContents];

    public void Reset() => _static.Clear();

    /// <summary>Everything carried and worn; empty when the character or its inventory isn't readable yet.</summary>
    public List<CarriedItem> Read(RynthCoreHost host, long nowMs)
    {
        var items = new List<CarriedItem>();
        if (!host.HasGetPlayerId || !host.HasGetContainerContents) return items;
        uint player = host.GetPlayerId();
        if (player == 0) return items;
        ScanInto(host, player, player, items, 0, nowMs);
        return items;
    }

    private void ScanInto(RynthCoreHost host, uint container, uint player, List<CarriedItem> items, int depth, long nowMs)
    {
        int n = host.GetContainerContents(container, _contents);
        if (n <= 0) return;
        uint[] ids = new uint[n];
        Array.Copy(_contents, ids, n);
        var packs = new List<uint>();
        foreach (uint id in ids)
        {
            if (id == 0) continue;
            Ownership(host, id, out uint c, out uint w, out uint loc);
            bool worn = w == player && w != 0;
            if (!worn && c != container && c != 0) continue;   // moved away since the engine's snapshot
            CarriedItem? it = ReadItem(host, id, nowMs, out Static? st);
            if (it == null) continue;
            if (worn)
            {
                if (loc == 0 && host.TryGetObjectIntProperty(id, CurrentWieldedLocation, out int l)) loc = unchecked((uint)l);
                it.WieldedLocation = loc == 0 ? 1 : unchecked((int)loc);   // worn but the slot unreadable: still "in hand"
            }
            else if (st!.IsContainer && depth < MaxNesting)
            {
                packs.Add(id);
                continue;   // a pack is not something to use or wield
            }
            items.Add(it);
        }
        foreach (uint pack in packs)
            ScanInto(host, pack, player, items, depth + 1, nowMs);
    }

    /// <summary>One item as the client has it now (null if it has no name yet or is gone).</summary>
    public CarriedItem? ReadOne(RynthCoreHost host, uint id, long nowMs)
    {
        uint player = host.HasGetPlayerId ? host.GetPlayerId() : 0;
        CarriedItem? it = ReadItem(host, id, nowMs, out _);
        if (it == null) return null;
        Ownership(host, id, out _, out uint w, out uint loc);
        if (w != 0 && w == player)
        {
            if (loc == 0 && host.TryGetObjectIntProperty(id, CurrentWieldedLocation, out int l)) loc = unchecked((uint)l);
            it.WieldedLocation = loc == 0 ? 1 : unchecked((int)loc);
        }
        return it;
    }

    private CarriedItem? ReadItem(RynthCoreHost host, uint id, long nowMs, out Static? st)
    {
        st = null;
        if (!_static.TryGetValue(id, out st) || Stale(st, nowMs))
        {
            st = ReadStatic(host, id, nowMs);
            if (st == null) return null;
            _static[id] = st;
        }
        var it = new CarriedItem
        {
            Id = id,
            Name = st.Name,
            Wcid = st.Wcid,
            Material = st.Material,
            ItemType = st.ItemType,
            Bitfield = st.Bitfield,
            MaxStack = st.MaxStack,
            MaxUses = st.MaxUses,
            ValidLocations = st.ValidLocations,
            CombatUse = st.CombatUse,
            DamageType = st.DamageType,
            UiEffects = st.UiEffects,
            Icon = st.Icon,
            Underlay = st.Underlay,
            Overlay = st.Overlay,
        };
        if (host.TryGetObjectIntProperty(id, StackSize, out int stack) && stack > 0) it.Stack = stack;
        if (st.MaxUses > 0 && host.TryGetObjectIntProperty(id, Structure, out int uses)) it.Uses = Math.Max(0, uses);
        return it;
    }

    private static bool Stale(Static st, long nowMs) =>
        (st.ItemType & ItemKinds.TypeCaster) != 0 && st.DamageType == 0 && nowMs - st.ReadMs >= RecheckMs;

    private static Static? ReadStatic(RynthCoreHost host, uint id, long nowMs)
    {
        if (!host.TryGetObjectName(id, out string name) || string.IsNullOrWhiteSpace(name)) return null;
        var st = new Static { Name = name.Trim(), ReadMs = nowMs };
        if (host.TryGetItemType(id, out uint type)) st.ItemType = type;
        st.IsContainer = (st.ItemType & ItemTypeContainer) != 0;
        if (host.HasGetObjectWcid && host.TryGetObjectWcid(id, out uint wcid)) st.Wcid = wcid;
        if (host.HasGetObjectBitfield && host.TryGetObjectBitfield(id, out uint bf)) st.Bitfield = bf;
        if (host.TryGetObjectIntProperty(id, MaterialType, out int material)) st.Material = Math.Max(0, material);
        if (host.TryGetObjectIntProperty(id, MaxStackSize, out int maxStack)) st.MaxStack = Math.Max(1, maxStack);
        if (host.TryGetObjectIntProperty(id, MaxStructure, out int maxUses) && maxUses > 0) st.MaxUses = maxUses;
        if (host.TryGetObjectIntProperty(id, ValidLocations, out int valid)) st.ValidLocations = valid;
        if (host.TryGetObjectIntProperty(id, CombatUse, out int use)) st.CombatUse = use;
        if (host.TryGetObjectIntProperty(id, DamageType, out int dt)) st.DamageType = dt;
        if (host.TryGetObjectIntProperty(id, UiEffects, out int fx)) st.UiEffects = fx;
        if (host.HasGetObjectDataIdProperty)
        {
            if (host.TryGetObjectDataIdProperty(id, DidIcon, out uint icon)) st.Icon = icon;
            if (host.TryGetObjectDataIdProperty(id, DidIconUnderlay, out uint under)) st.Underlay = under;
            if (host.TryGetObjectDataIdProperty(id, DidIconOverlay, out uint over)) st.Overlay = over;
        }
        return st;
    }

    private static void Ownership(RynthCoreHost host, uint id, out uint container, out uint wielder, out uint location)
    {
        container = wielder = location = 0;
        if (host.HasGetObjectOwnershipInfo && host.TryGetObjectOwnershipInfo(id, out container, out wielder, out location)) return;
        if (host.HasGetObjectWielderInfo && host.TryGetObjectWielderInfo(id, out uint w, out uint loc)) { wielder = w; location = loc; }
    }
}

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using RynthCore.Plugin.RynthLua;

namespace RynthLua.CompatTests;

/// <summary>One object the fake client knows.</summary>
internal sealed class FakeObject
{
    public uint Id;
    public string Name = string.Empty;
    public uint Wcid;
    public uint ItemType;
    public uint Bitfield;
    public uint Container, Wielder, Location;
    public uint Cell;
    public float X, Y, Z, Heading;
    public readonly Dictionary<uint, int> Ints = new();
    public readonly Dictionary<uint, long> Int64s = new();
    public readonly Dictionary<uint, double> Floats = new();
    public readonly Dictionary<uint, string> Strings = new();
    public readonly Dictionary<uint, bool> Bools = new();
    public readonly Dictionary<uint, uint> DataIds = new();
    public readonly Dictionary<uint, uint> InstanceIds = new();   // besides Container/Wielder
    public readonly List<uint> Spells = new();
    public bool Appraised;
    public long LastIdTime;
    public uint PhysicsState;

    public int Stack
    {
        get => Ints.TryGetValue(12, out int v) ? v : 1;
        set => Ints[12] = value;
    }

    public bool HasPosition => Container == 0 && Wielder == 0 && Cell != 0;
}

/// <summary>
/// The scripted world behind the fake engine: a player ("Tester") with vitals, skills,
/// attributes, spells, enchantments and a small inventory, a few landscape objects (a
/// monster, an NPC, a portal, a lever, a chest, another player, a vendor), and a very
/// small "server" that answers the client actions one step later (UseDone, moves, splits,
/// wields, casts, vendor and trade). Events the engine would raise (object created,
/// selection, chat, ...) are queued and delivered to the plugin between ticks, the way the
/// engine's pump does.
/// </summary>
internal sealed class FakeWorld
{
    public static FakeWorld W = new();

    // Fixture ids (also given to scripts as harness.ids).
    public const uint Player = 0x50000001;
    public const uint OtherPlayer = 0x50000002;
    public const uint Potion = 0x80000101;
    public const uint Pyreals = 0x80000102;
    public const uint Backpack = 0x80000103;
    public const uint ManaStone = 0x80000104;
    public const uint Tapers = 0x80000105;
    public const uint Ust = 0x80000106;
    public const uint Sword = 0x80000107;
    public const uint Wand = 0x80000110;
    public const uint Cap = 0x80000111;
    public const uint Drudge = 0x80000201;
    public const uint Crier = 0x80000202;
    public const uint PortalObj = 0x80000203;
    public const uint Lever = 0x80000204;
    public const uint Chest = 0x80000205;
    public const uint Shopkeeper = 0x80000206;
    public const uint ChestGem = 0x80000301;
    public const uint VendorItemA = 0x80000401;   // "Mana Potion" on the vendor's list
    public const uint VendorItemB = 0x80000402;   // "Iron Arrowhead"

    public const uint HomeCell = 0x7D640013;
    public const float HomeX = 100f, HomeY = 100f, HomeZ = 10f;

    public readonly Dictionary<uint, FakeObject> Objects = new();
    public uint Selected, PreviousSelected, GroundContainer;
    public int CombatMode = 1;
    public bool Portaling;
    public int PortalStepsLeft;
    public uint Hp = 300, MaxHp = 350, St = 400, MaxSt = 400, Mana = 250, MaxMana = 500;
    public uint BaseHp = 320, BaseSt = 380, BaseMana = 450;
    public float Vitae = 1f;
    public readonly Dictionary<int, (int Buffed, int Base, int Training)> Skills = new();
    public readonly Dictionary<int, (uint Cur, uint Base)> Attributes = new();
    public readonly List<(uint Spell, double Expiry)> Enchantments = new();
    public readonly List<uint> KnownSpells = new();
    public bool AutoRun;
    public int UseDoneSeq;
    public uint UseDoneError;
    public int RefusalSeq;
    public uint RefusalError, RefusalEvent, RefusalObject;
    public int BusyState, CastBusy;

    // What the client was asked to do.
    public readonly List<string> ChatOut = new();      // WriteToChat
    public readonly List<string> Invoked = new();      // InvokeChatParser
    public readonly List<string> Calls = new();        // "UseObject 0x80000101", ...
    public readonly List<string> Log = new();

    // Salvage panel.
    public uint SalvageTool;
    public readonly List<uint> SalvageItems = new();

    // Vendor.
    public uint VendorOpenId;
    public uint VendorGeneration;
    public readonly List<(uint Id, string Name, uint Wcid, uint ItemType, int Amount, int UnitPrice)> VendorItems = new();
    public uint VendorRequestId, VendorRequestSeq;
    public int VendorState, VendorResult, VendorIsBuy, VendorEntries;

    // Trade.
    public bool TradeOpen, TradeYouAccepted, TradePartnerAccepted;
    public uint TradeGeneration, TradeSequence, TradePartner, TradeInitiator, TradeLastEvent;
    public uint TradeCompleted, TradeLastClose, TradeLastAcceptedBy, TradeLastDeclinedBy, TradeLastResetBy, TradePartnerAccepts;
    public readonly List<uint> TradeSelf = new(), TradePartnerItems = new();

    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private long _step;
    private readonly List<(long Step, Action Act)> _pending = new();
    private uint _nextId = 0x80010000;

    /// <summary>Events for the plugin, delivered between ticks (the engine's pump order).</summary>
    public readonly Queue<Action<RynthLuaPlugin>> Events = new();

    public double ServerTime => 50_000.0 + _clock.Elapsed.TotalSeconds;

    public FakeWorld()
    {
        var p = Add(Player, "Tester", 0, itemType: 0x10, bitfield: 0x8);
        p.Cell = HomeCell; p.X = HomeX; p.Y = HomeY; p.Z = HomeZ; p.Heading = 90f;
        p.Ints[25] = 100;          // Level
        p.Ints[5] = 1500;          // EncumbranceVal
        p.Ints[20] = 25000;        // CoinValue
        p.Int64s[1] = 1_000_000;   // TotalExperience
        p.InstanceIds[26] = OtherPlayer;   // Monarch (PlayerDescription's instance id table)
        p.Int64s[2] = 50_000;      // AvailableExperience
        p.Int64s[6] = 5_000;       // AvailableLuminance
        p.Int64s[7] = 100_000;     // MaximumLuminance
        p.Strings[1] = "Tester";
        p.Strings[5] = "Adventurer";   // Template

        var potion = Add(Potion, "Healing Potion", 2457, itemType: 0x20, container: Player);
        potion.Stack = 5; potion.Ints[11] = 100; potion.Ints[19] = 100; potion.Ints[5] = 25;
        var pyr = Add(Pyreals, "Pyreal", 273, itemType: 0x40, container: Player);
        pyr.Stack = 2500; pyr.Ints[11] = 25000; pyr.Ints[19] = 2500;
        Add(Backpack, "Backpack", 136, itemType: 0x200, container: Player);
        var stone = Add(ManaStone, "Mana Stone", 2434, itemType: 0x80000, container: Backpack);
        stone.Ints[19] = 50;
        var tapers = Add(Tapers, "Prismatic Taper", 20631, itemType: 0x1000, container: Backpack);
        tapers.Stack = 100; tapers.Ints[11] = 5000; tapers.Ints[19] = 100;
        Add(Ust, "Ust", 20646, itemType: 0x20000000, container: Player);
        var sword = Add(Sword, "Plain Sword", 351, itemType: 0x1, container: Player);
        sword.Ints[9] = 0x00100000;   // ValidLocations: MeleeWeapon
        sword.Ints[19] = 500;
        sword.Ints[131] = 64;         // MaterialType: Steel
        sword.Floats[21] = 1.25;      // WeaponLength
        sword.Bools[22] = true;       // Inscribable
        sword.Strings[16] = "A plain sword.";   // LongDesc
        sword.DataIds[8] = 0x06001234;          // Icon
        sword.Int64s[4] = 0;                    // ItemTotalXp
        var wand = Add(Wand, "Wand of Testing", 12345, itemType: 0x8000, wielder: Player, location: 0x01000000);
        wand.Ints[9] = 0x01000000;
        wand.DataIds[28] = 2345;      // Spell
        wand.Spells.Add(2345);
        var cap = Add(Cap, "Leather Cap", 118, itemType: 0x2, wielder: Player, location: 0x1);
        cap.Ints[9] = 0x1;
        cap.Ints[28] = 45;            // ArmorLevel

        Place(Add(Drudge, "Drudge Skulker", 7, itemType: 0x10, bitfield: 0x10), 10, 0, 0);
        Place(Add(Crier, "Town Crier", 22, itemType: 0x10, bitfield: 0x0), 8, 0, 0);
        Place(Add(PortalObj, "Portal to Town", 1955, itemType: 0x10000, bitfield: 0x40000), 20, 0, 30).PhysicsState = 0x0C;
        Place(Add(Lever, "Tower Lever", 3000, itemType: 0x80), 5, 0, 40);
        Place(Add(Chest, "Chest", 3001, itemType: 0x200), 3, 3, 0);
        var gem = Add(ChestGem, "Amethyst", 2400, itemType: 0x800, container: Chest);
        gem.Ints[19] = 750;
        var other = Place(Add(OtherPlayer, "Friendly Player", 1, itemType: 0x10, bitfield: 0x8), 4, -4, 0);
        other.Strings[1] = "Friendly Player";
        Place(Add(Shopkeeper, "Shopkeeper", 4000, itemType: 0x10, bitfield: 0x200), 6, 6, 0);

        foreach (var (id, b, t) in new[] { (6, 350, 3), (34, 380, 2), (40, 200, 2), (1, 50, 1), (11, 0, 0), (12, 120, 2), (26, 90, 2), (47, 300, 3) })
            Skills[id] = (b + (t >= 2 ? 50 : 0), b, t);
        Attributes[1] = (200, 190); Attributes[2] = (150, 150); Attributes[3] = (180, 170);
        Attributes[4] = (170, 170); Attributes[5] = (160, 150); Attributes[6] = (190, 180);

        KnownSpells.AddRange(new uint[] { 1234, 2345, 3456, 4567 });
        Enchantments.Add((3456, ServerTime + 600));
        Enchantments.Add((5000, double.MaxValue));
    }

    // ── Building ────────────────────────────────────────────────────────────

    public FakeObject Add(uint id, string name, uint wcid, uint itemType = 0, uint bitfield = 0,
        uint container = 0, uint wielder = 0, uint location = 0)
    {
        var o = new FakeObject
        {
            Id = id, Name = name, Wcid = wcid, ItemType = itemType, Bitfield = bitfield,
            Container = container, Wielder = wielder, Location = location,
        };
        o.Strings[1] = name;
        Objects[id] = o;
        return o;
    }

    private static FakeObject Place(FakeObject o, float dx, float dy, float dz)
    {
        o.Cell = HomeCell; o.X = HomeX + dx; o.Y = HomeY + dy; o.Z = HomeZ + dz;
        return o;
    }

    public uint NewId() => _nextId++;

    public FakeObject? Get(uint id) => Objects.TryGetValue(id, out var o) ? o : null;

    public FakeObject Me => Objects[Player];

    /// <summary>A new object arrives from the server (plugin sees OnCreateObject).</summary>
    public FakeObject Create(FakeObject o)
    {
        Objects[o.Id] = o;
        Events.Enqueue(p => p.OnCreateObject(o.Id));
        return o;
    }

    /// <summary>The server releases an object (plugin sees OnDeleteObject).</summary>
    public void Delete(uint id)
    {
        if (!Objects.Remove(id)) return;
        if (Selected == id) Selected = 0;
        Events.Enqueue(p => p.OnDeleteObject(id));
    }

    public List<uint> Contents(uint container) =>
        Objects.Values.Where(o => o.Container == container && o.Wielder == 0).Select(o => o.Id).OrderBy(i => i).ToList();

    public bool IsMine(uint id)
    {
        var o = Get(id);
        if (o == null) return false;
        if (o.Wielder == Player || o.Container == Player) return true;
        var c = Get(o.Container);
        return c != null && c.Container == Player;
    }

    // ── The small server ────────────────────────────────────────────────────

    /// <summary>Runs <paramref name="act"/> on the next step (the server answering).</summary>
    public void Later(Action act, int steps = 1) => _pending.Add((_step + steps, act));

    public void Step()
    {
        _step++;
        var due = _pending.Where(p => p.Step <= _step).ToList();
        _pending.RemoveAll(p => p.Step <= _step);
        foreach (var (_, act) in due) act();
        if (Portaling && --PortalStepsLeft <= 0) Portaling = false;
    }

    public void UseDone(uint error = 0)
    {
        UseDoneSeq++;
        UseDoneError = error;
    }

    public void Refuse(uint code, uint eventType = 0x028A, uint objectId = 0)
    {
        RefusalSeq++;
        RefusalError = code;
        RefusalEvent = eventType;
        RefusalObject = objectId;
    }

    public void IncomingChat(string text, int type) => Events.Enqueue(p =>
    {
        int eat = 0;
        p.OnChatWindowText(text, type, ref eat);
    });

    public void SetSelected(uint id)
    {
        uint prev = Selected;
        PreviousSelected = prev;
        Selected = id;
        Events.Enqueue(p => p.OnSelectedTargetChange(id, prev));
    }

    // Client actions (called from the fake engine functions) ─────────────────

    public bool UseObject(uint id, uint target = 0)
    {
        var o = Get(id);
        Calls.Add(target == 0 ? $"UseObject 0x{id:X8}" : $"UseObjectOn 0x{id:X8} 0x{target:X8}");
        if (o == null) return false;
        Later(() =>
        {
            if (o.ItemType == 0x20 && o.Name.Contains("Potion"))
            {
                Hp = Math.Min(MaxHp, Hp + 50);
                if (o.Stack > 1) o.Stack--;
                else Delete(o.Id);
            }
            else if (o.Ints.TryGetValue(9, out int valid) && valid != 0 && o.Wielder == 0 && IsMine(o.Id))
            {
                o.Container = 0; o.Wielder = Player; o.Location = (uint)valid;
            }
            else if (o.ItemType == 0x200 && o.Container == 0 && !IsMine(o.Id))
            {
                uint was = GroundContainer;
                if (was != 0) Events.Enqueue(p => p.OnStopViewingObjectContents(was));
                GroundContainer = o.Id;
                Events.Enqueue(p => p.OnViewObjectContents(o.Id));
            }
            else if (o.ItemType == 0x20000000)
                SalvageTool = o.Id;   // using an Ust opens the salvage panel
            else if ((o.Bitfield & 0x40000) != 0)
            {
                Portaling = true;
                PortalStepsLeft = 5;
            }
            UseDone();
        });
        return true;
    }

    public bool MoveInternal(uint id, uint container, int slot, int amount)
    {
        Calls.Add($"MoveItemInternal 0x{id:X8} 0x{container:X8} {slot} {amount}");
        var o = Get(id);
        if (o == null) return false;
        Later(() => { o.Container = container; o.Wielder = 0; o.Location = 0; });
        return true;
    }

    public bool MoveExternal(uint id, uint target, int amount)
    {
        Calls.Add($"MoveItemExternal 0x{id:X8} 0x{target:X8} {amount}");
        var o = Get(id);
        if (o == null) return false;
        Later(() =>
        {
            if (target == 0)
            {
                o.Container = 0; o.Wielder = 0; o.Location = 0;
                o.Cell = HomeCell; o.X = HomeX + 1; o.Y = HomeY; o.Z = HomeZ;
            }
            else if (Get(target) is { } t && (t.ItemType & 0x10) != 0)
                Delete(o.Id);   // handed to a creature
            else
            {
                o.Container = target; o.Wielder = 0; o.Cell = 0;
            }
        });
        return true;
    }

    public bool Give(uint id, uint target, int amount)
    {
        Calls.Add($"GiveObjectTo 0x{id:X8} 0x{target:X8} {amount}");
        if (Get(id) == null) return false;
        Later(() => Delete(id));
        return true;
    }

    public bool Split(uint id, uint container, int slot, int amount)
    {
        Calls.Add($"SplitStackInternal 0x{id:X8} 0x{container:X8} {slot} {amount}");
        var o = Get(id);
        if (o == null || amount >= o.Stack) return false;
        Later(() =>
        {
            o.Stack -= amount;
            var n = new FakeObject { Id = NewId(), Name = o.Name, Wcid = o.Wcid, ItemType = o.ItemType, Container = container };
            foreach (var kv in o.Ints) n.Ints[kv.Key] = kv.Value;
            n.Strings[1] = o.Name;
            n.Stack = amount;
            Create(n);
        });
        return true;
    }

    public bool Merge(uint src, uint dst)
    {
        Calls.Add($"MergeStackInternal 0x{src:X8} 0x{dst:X8}");
        var s = Get(src); var d = Get(dst);
        if (s == null || d == null) return false;
        Later(() => { d.Stack += s.Stack; Delete(src); });
        return true;
    }

    public bool Wield(uint id, uint mask)
    {
        Calls.Add($"WieldItem 0x{id:X8} 0x{mask:X8}");
        var o = Get(id);
        if (o == null) return false;
        Later(() => { o.Container = 0; o.Wielder = Player; o.Location = mask; });
        return true;
    }

    public bool Cast(uint target, int spell)
    {
        Calls.Add($"CastSpell {spell} 0x{target:X8}");
        Later(() =>
        {
            string on = target != 0 && Get(target) is { } t ? " on " + t.Name : string.Empty;
            IncomingChat($"You cast Spell {spell}{on}.", 7);
            UseDone();
        });
        return true;
    }

    public bool ChangeCombat(int mode)
    {
        Calls.Add($"ChangeCombatMode {mode}");
        Later(() =>
        {
            int prev = CombatMode;
            CombatMode = mode;
            Events.Enqueue(p => p.OnCombatModeChange(mode, prev));
        });
        return true;
    }

    public bool RequestId(uint id)
    {
        Calls.Add($"RequestId 0x{id:X8}");
        var o = Get(id);
        if (o == null) return false;
        Later(() => { o.Appraised = true; o.LastIdTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + (long)_step; });
        return true;
    }

    public bool Salvage()
    {
        Calls.Add($"SalvagePanelExecute {SalvageItems.Count}");
        if (SalvageTool == 0 || SalvageItems.Count == 0) return false;
        var items = SalvageItems.ToList();
        SalvageItems.Clear();
        Later(() =>
        {
            foreach (uint id in items) Delete(id);
            var bag = new FakeObject { Id = NewId(), Name = "Salvaged Steel", Wcid = 20993, ItemType = 0x40000000, Container = Player };
            bag.Strings[1] = bag.Name;
            Create(bag);
            UseDone();
        });
        return true;
    }

    // Vendor ──────────────────────────────────────────────────────────────

    public void OpenVendor(uint id)
    {
        VendorOpenId = id;
        VendorGeneration++;
        VendorItems.Clear();
        VendorItems.Add((VendorItemA, "Mana Potion", 2460, 0x20, -1, 80));
        VendorItems.Add((VendorItemB, "Iron Arrowhead", 5000, 0x2000000, 500, 2));
        Events.Enqueue(p => p.OnVendorOpen(id));
    }

    public void CloseVendor()
    {
        uint id = VendorOpenId;
        VendorOpenId = 0;
        Events.Enqueue(p => p.OnVendorClose(id));
    }

    public uint VendorTrade(bool buy, List<(uint Id, int Amount)> lines)
    {
        Calls.Add($"Vendor{(buy ? "Buy" : "Sell")} {string.Join(",", lines.Select(l => $"0x{l.Id:X8}x{l.Amount}"))}");
        if (VendorOpenId == 0 || lines.Count == 0) return 0;
        uint req = ++VendorRequestSeq;
        VendorRequestId = req;
        VendorState = 2; VendorResult = 0; VendorIsBuy = buy ? 1 : 0; VendorEntries = lines.Count;
        Later(() =>
        {
            foreach (var (id, amount) in lines)
            {
                if (buy)
                {
                    var item = VendorItems.First(v => v.Id == id);
                    var n = new FakeObject { Id = NewId(), Name = item.Name, Wcid = item.Wcid, ItemType = item.ItemType, Container = Player };
                    n.Strings[1] = item.Name;
                    n.Stack = amount;
                    Create(n);
                }
                else Delete(id);
            }
            VendorGeneration++;
            VendorState = 3; VendorResult = 1;
        });
        return req;
    }

    // Trade ───────────────────────────────────────────────────────────────

    public void StartTrade(uint partner)
    {
        TradeOpen = true;
        TradeGeneration++;
        TradeSequence++;
        TradePartner = partner;
        TradeInitiator = partner;
        TradeLastEvent = 0x01FD;
        TradeYouAccepted = TradePartnerAccepted = false;
        TradeSelf.Clear();
        TradePartnerItems.Clear();
    }

    public void TradeEvent(uint evt, Action change)
    {
        change();
        TradeSequence++;
        TradeLastEvent = evt;
    }
}

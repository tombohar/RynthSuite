using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using RynthCore.Plugin.RynthAi;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.Plugin.RynthAi.Vendor;
using RynthCore.PluginSdk;
using RynthCore.RynthAiHostTests.Fakes;
using RynthCore.RynthAiTests.Harness;

namespace RynthCore.RynthAiHostTests.Tests;

// 2026-11-01, Master Raiden Stormcaller (Conquest): AutoVendor had to sell one Trade Note (250,000)
// to pay for Prismatic Tapers. It split one note off, saw the new stack and split again 0.3 s later:
// the profile's "B: Trade Note (250,000)" Keep rule (ObjectClass TradeNote, Value = 250,000; written
// for the vendor's listing) also matches a one-note stack, so the new stack (and any note split off
// by hand) never made the sell list. The second split went onto the first one's stack: "made no new
// stack". Now a split's result is known from the server's create (or the stack it joined), the stack
// it made is sold past the rules, loose single notes count as "already have one", one split is in
// flight at a time, and a split the vendor closed on is picked up at the next open.
//
// The manager's clock (AutoVendorManager.Clock) is moved by hand; the "server" answers in the fakes.
internal static unsafe class AutoVendorSplitTests
{
    private const uint Player = 0x50000A01;
    private const uint Vendor = 0x70000001;   // FakeHost's open vendor id
    private const uint Taper = 0x70000101;    // the vendor's listing
    private const uint Notes = 0x80A00001, Tapers = 0x80A00002, Letter = 0x80A00003;
    private const uint NewStack = 0x80A00101, HandSingle = 0x80A00102;
    private const uint NoteWcid = 20630, TaperWcid = 20631;
    private const uint TypeNote = 0x00040000, TypeComp = 0x00001000, TypeWritable = 0x00002000;
    private const int NoteValue = 250_000;

    public static void Register(Runner r)
    {
        r.Add("autovendor split: the split-off note is identified and sold, never split twice, then the buy goes through", SplitOffNoteIsSold);
        r.Add("autovendor split: a note split off by hand is sold before any split", HandSplitSingleIsSold);
        r.Add("autovendor split: a split the vendor closed on is picked up at the next open, no second split", SplitResumesNextOpen);
        r.Add("autovendor split: a split that went onto another stack is recognised, not 'made no new stack'", SplitJoinedStack);
        r.Add("autovendor split: no second split while one is in flight", NoSecondSplitInFlight);
        r.Add("autovendor split: a session that can't buy says why (chat and log)", CantBuySaysWhy);
        r.Add("autovendor split: a custom letter is no TradeNote (no 'needs Object Class data' line)", LetterIsNoTradeNote);
    }

    // ── The fake vendor and server ────────────────────────────────────────────

    private static int _coins;
    private static uint _lastRequest;
    private static readonly List<(uint Id, uint Container, int Slot, int Amount)> Splits = new();
    private static readonly List<uint[]> Sells = new();
    private static readonly List<(uint Id, int Amount)[]> Buys = new();
    private static Action<uint, int>? _onSplit;
    private static Action<uint[]>? _onSell;
    private static Action<(uint Id, int Amount)[]>? _onBuy;

    private static void Configure(ref RynthCoreApiNative api)
    {
        api.Version = Math.Max(api.Version, 82u);
        api.GetVendorInfoFn        = (IntPtr)(delegate* unmanaged[Cdecl]<VendorInfoNative*, int>)&GetVendorInfo;
        api.GetVendorItemsFn       = (IntPtr)(delegate* unmanaged[Cdecl]<VendorItemNative*, int, int>)&GetVendorItems;
        api.VendorBuyFn            = (IntPtr)(delegate* unmanaged[Cdecl]<uint, VendorTradeEntryNative*, int, uint>)&VendorBuy;
        api.VendorSellFn           = (IntPtr)(delegate* unmanaged[Cdecl]<uint, uint*, int, uint>)&VendorSell;
        api.GetVendorTradeStatusFn = (IntPtr)(delegate* unmanaged[Cdecl]<VendorTradeStatusNative*, int>)&GetVendorTradeStatus;
        api.SplitStackInternalFn   = (IntPtr)(delegate* unmanaged[Cdecl]<uint, uint, int, int, int>)&SplitStackInternal;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int GetVendorInfo(VendorInfoNative* info)
    {
        *info = default;
        info->VendorId = Vendor;
        info->ItemTypes = TypeNote | TypeComp | TypeWritable;
        info->MaxValue = 1_000_000;
        info->BuyRate = 0.7f;
        info->SellRate = 1.15f;
        info->PlayerCoins = _coins;
        info->ItemCount = 1;
        info->Flags = 1;   // trading available
        byte[] name = Encoding.ASCII.GetBytes("Town Vendor");
        for (int i = 0; i < name.Length; i++) info->Name[i] = name[i];
        return 1;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int GetVendorItems(VendorItemNative* items, int max)
    {
        if (items == null || max < 1) return 1;
        items[0] = default;
        items[0].ObjectId = Taper;
        items[0].ItemType = TypeComp;
        items[0].Amount = -1;
        items[0].StackSize = 1;
        items[0].MaxStackSize = 5000;
        items[0].Value = 100;
        items[0].UnitValue = 100;
        items[0].UnitPrice = 115;
        items[0].Flags = 1 | 2;   // unlimited, stackable
        byte[] name = Encoding.ASCII.GetBytes("Prismatic Taper");
        for (int i = 0; i < name.Length; i++) items[0].Name[i] = name[i];
        return 1;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static uint VendorBuy(uint vendor, VendorTradeEntryNative* entries, int count)
    {
        var list = new (uint, int)[count];
        for (int i = 0; i < count; i++) list[i] = (entries[i].ObjectId, entries[i].Amount);
        Buys.Add(list);
        _onBuy?.Invoke(list);
        return ++_lastRequest;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static uint VendorSell(uint vendor, uint* ids, int count)
    {
        var list = new uint[count];
        for (int i = 0; i < count; i++) list[i] = ids[i];
        Sells.Add(list);
        _onSell?.Invoke(list);
        return ++_lastRequest;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int GetVendorTradeStatus(VendorTradeStatusNative* status)
    {
        *status = default;
        if (_lastRequest == 0) return 0;
        status->RequestId = _lastRequest;
        status->State = (int)VendorTradeState.Done;
        status->Result = (int)VendorTradeResult.Accepted;
        status->VendorId = Vendor;
        return 1;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int SplitStackInternal(uint id, uint container, int slot, int amount)
    {
        Splits.Add((id, container, slot, amount));
        _onSplit?.Invoke(id, amount);
        return 1;
    }

    // ── World ─────────────────────────────────────────────────────────────────

    private const string Profile =
        "UTL\r\n1\r\n3\r\n" +
        "B: Prismatic Tapers\r\n\r\n0;10;7;1\r\n3000\r\n4\r\n12\r\n20\r\nPrismatic Taper\r\n1\r\n" +
        "B: Trade Note (250,000)\r\n\r\n0;1;7;12\r\n4\r\n15\r\n12\r\n250000\r\n19\r\n" +
        "S: Trade Note\r\n\r\n0;3;7\r\n4\r\n15\r\n";

    private static long _now;

    private static void AddStack(uint id, string name, uint type, uint wcid, int count, int unitValue, int max, bool appraised = true)
    {
        FakeHost.Names[id] = name;
        FakeHost.ItemTypes[id] = type;
        FakeHost.Wcids[id] = wcid;
        FakeHost.Containers[id] = Player;
        SetCount(id, count, unitValue);
        FakeHost.Ints[(id, 11)] = max;
        if (appraised) FakeHost.Appraised.Add(id);
    }

    private static void SetCount(uint id, int count, int unitValue)
    {
        FakeHost.Ints[(id, 12)] = count;
        FakeHost.Ints[(id, 19)] = count * unitValue;
    }

    private static int Count(uint id) => FakeHost.Ints.TryGetValue((id, 12), out int c) ? c : 0;

    private static void Remove(uint id)
    {
        FakeHost.Containers.Remove(id);
        FakeHost.Names.Remove(id);
    }

    private sealed class Rig
    {
        public AutoVendorManager M = null!;
        public WorldObjectCache Cache = null!;
        public LegacyUiSettings Settings = null!;
        public string ProfilePath = string.Empty;
    }

    /// <summary>40 notes and 2,964 of 3,000 wanted tapers in the pack, no pyreals.</summary>
    private static Rig World(bool withNotes = true)
    {
        FakeHost.Reset();
        Splits.Clear(); Sells.Clear(); Buys.Clear();
        _onSplit = null; _onSell = null; _onBuy = null;
        _coins = 0; _lastRequest = 0;
        FakeHost.WeaponCalls = true;    // RequestId, HasAppraisalData
        FakeHost.ContentsCalls = true;  // the pack walk
        FakeHost.Names[Player] = "Test Trader";
        FakeHost.Names[Vendor] = "Town Vendor";
        if (withNotes) AddStack(Notes, "Trade Note (250,000)", TypeNote, NoteWcid, 40, NoteValue, 250);
        AddStack(Tapers, "Prismatic Taper", TypeComp, TaperWcid, 2964, 100, 5000);
        FakeHost.ConfigureApi = Configure;
        RynthCoreHost host = FakeHost.Create(Player);

        var rig = new Rig();
        rig.Cache = new WorldObjectCache(host);
        rig.Cache.SetPlayerId(Player);
        rig.Settings = new LegacyUiSettings { AutoVendorEnabled = true, AutoVendorTestMode = false, AutoVendorShowMerchantInfo = false };
        // The character folder is the first place a vendor's profile is looked for: the test's
        // profile there, named for the vendor, is the one every open loads.
        string charDir = Path.Combine(Program.TempRoot, "autovendor-char");
        Directory.CreateDirectory(Path.Combine(charDir, AutoVendorManager.ProfileSubfolder));
        rig.ProfilePath = Path.Combine(charDir, AutoVendorManager.ProfileSubfolder, "Town Vendor.utl");
        File.WriteAllText(rig.ProfilePath, Profile);
        rig.M = new AutoVendorManager(host, rig.Settings, rig.Cache, Player, () => charDir);
        return rig;
    }

    /// <summary>The vendor opens (or its list is re-sent): AutoVendor starts when it isn't running.</summary>
    private static void Open(Rig rig) => rig.M.OnVendorOpen(Vendor);

    private static readonly FieldInfo ScanAt = typeof(WorldObjectCache).GetField("_lastDirectInventoryScan", BindingFlags.Instance | BindingFlags.NonPublic)!;

    /// <summary>Moves the clock and ticks; the pack walk always reads the fakes fresh.</summary>
    private static void Run(Rig rig, int ticks, long stepMs = 100)
    {
        for (int i = 0; i < ticks; i++)
        {
            _now += stepMs;
            ScanAt.SetValue(rig.Cache, DateTime.MinValue);
            rig.M.Tick(0);
        }
    }

    private static void Clocked(Action body)
    {
        var old = AutoVendorManager.Clock;
        _now = 50_000_000;
        AutoVendorManager.Clock = () => _now;
        try { body(); }
        finally { AutoVendorManager.Clock = old; FakeHost.ConfigureApi = null; }
    }

    /// <summary>The server's split: a new stack of <paramref name="amount"/>, not identified yet.</summary>
    private static void ServerSplitsToNewStack(Rig rig, uint source, int amount, uint newId)
    {
        SetCount(source, Count(source) - amount, NoteValue);
        AddStack(newId, "Trade Note (250,000)", TypeNote, NoteWcid, amount, NoteValue, 250, appraised: false);
        rig.M.OnCreateObject(newId);
    }

    /// <summary>Selling a note pays 250,000 and the note leaves the pack; a taper buy lands.</summary>
    private static void ServerTrades()
    {
        _onSell = ids => { foreach (uint id in ids) { _coins += NoteValue * Math.Max(1, Count(id)); Remove(id); } };
        _onBuy = lines => { foreach (var (id, amount) in lines) if (id == Taper) { SetCount(Tapers, Count(Tapers) + amount, 100); _coins -= 115 * amount; } };
    }

    // ── Tests ─────────────────────────────────────────────────────────────────

    private static void SplitOffNoteIsSold() => Clocked(() =>
    {
        var rig = World();
        ServerTrades();
        _onSplit = (id, amount) => ServerSplitsToNewStack(rig, id, amount, NewStack);
        Open(rig);
        Run(rig, 10);

        Check.Eq(Splits.Count, 1, "one split sent");
        Check.Eq(Splits[0].Id, Notes, "off the 40-note stack");
        Check.Eq(Splits[0].Amount, 1, "one note");
        Check.True(FakeHost.Logs.Any(l => l.Contains("new stack 0x80A00101")), "the new stack is known from the server's create");
        Check.True(FakeHost.IdRequests.Contains(NewStack), "the new stack is identified");
        Check.Eq(Sells.Count, 0, "nothing sold before the ID");

        // Still no ID: AutoVendor waits instead of splitting a second note onto the first one's stack.
        Run(rig, 10);
        Check.Eq(Splits.Count, 1, "no second split while the split-off note is being identified");
        Check.True(FakeHost.Logs.Any(l => l.Contains("waiting for the ID of Trade Note (250,000) 0x80A00101")), "the wait is logged");

        FakeHost.Appraised.Add(NewStack);
        Run(rig, 20);
        Check.Eq(Sells.Count, 1, "one sale");
        Check.True(Sells.Count > 0 && Sells[0].SequenceEqual(new[] { NewStack }), "the split-off note, though the Keep rule matches a one-note stack");
        Check.Eq(Buys.Count, 1, "then the buy");
        Check.True(Buys.Count > 0 && Buys[0].SequenceEqual(new[] { (Taper, 36) }), "36 tapers: 3,000 - 2,964");
        Check.Eq(Splits.Count, 1, "still one split");
        Check.Eq(Count(Notes), 39, "39 notes left");
        Check.False(FakeHost.Logs.Any(l => l.Contains("made no new stack") || l.Contains("made no stack")), "no split failure");
    });

    private static void HandSplitSingleIsSold() => Clocked(() =>
    {
        var rig = World();
        ServerTrades();
        AddStack(HandSingle, "Trade Note (250,000)", TypeNote, NoteWcid, 1, NoteValue, 250);   // split off by hand
        _onSplit = (id, amount) => ServerSplitsToNewStack(rig, id, amount, NewStack);
        Open(rig);
        Run(rig, 20);

        Check.Eq(Splits.Count, 0, "no split: a single note is already there");
        Check.True(Sells.Count > 0 && Sells[0].SequenceEqual(new[] { HandSingle }), "the loose note is sold");
        Check.Eq(Buys.Count, 1, "and the tapers bought");
        Check.Eq(Count(Notes), 40, "the 40-note stack untouched");
    });

    private static void SplitResumesNextOpen() => Clocked(() =>
    {
        var rig = World();
        ServerTrades();
        Open(rig);
        Run(rig, 5);
        Check.Eq(Splits.Count, 1, "the split was sent");

        // The vendor closes before the server answers; the new stack arrives afterwards.
        rig.M.OnVendorClose(Vendor);
        Run(rig, 2);
        ServerSplitsToNewStack(rig, Notes, 1, NewStack);
        FakeHost.Appraised.Add(NewStack);
        Run(rig, 5);
        Check.True(FakeHost.Logs.Any(l => l.Contains("new stack 0x80A00101")), "the stack is recognised with no session running");

        // Next open: the split-off note is sold, nothing split again.
        Open(rig);
        Run(rig, 25);
        Check.Eq(Splits.Count, 1, "no second split at the next open");
        Check.True(Sells.Count > 0 && Sells[0].SequenceEqual(new[] { NewStack }), "the note split off last time is sold");
        Check.Eq(Buys.Count, 1, "and the buy goes through");
    });

    private static void SplitJoinedStack() => Clocked(() =>
    {
        var rig = World();
        ServerTrades();
        // A loose note (split off by hand) sits at the split's slot, and its ID never comes back.
        AddStack(HandSingle, "Trade Note (250,000)", TypeNote, NoteWcid, 1, NoteValue, 250, appraised: false);
        bool resolvedBeforeSecond = true;
        _onSplit = (id, amount) =>
        {
            if (Splits.Count >= 2 && !FakeHost.Logs.Any(l => l.Contains("went onto stack 0x80A00102")))
                resolvedBeforeSecond = false;
            // The server puts the split-off units onto the stack at the slot: no new object.
            SetCount(id, Count(id) - amount, NoteValue);
            SetCount(HandSingle, Count(HandSingle) + amount, NoteValue);
        };
        Open(rig);
        Run(rig, 200);   // the session's ID pass and the loose note's IDs run out first (~9 s)

        Check.True(Splits.Count >= 1, "the split was sent once the loose note's ID gave up");
        Check.True(FakeHost.Logs.Any(l => l.Contains("waiting for the ID of Trade Note (250,000) 0x80A00102")), "it waited for that ID first");
        Check.True(FakeHost.Logs.Any(l => l.Contains("no new object; the units went onto stack 0x80A00102 (now x2)")), "the joined stack is recognised");
        Check.False(FakeHost.Logs.Any(l => l.Contains("made no new stack")), "not the old 'made no new stack'");
        Check.True(resolvedBeforeSecond, "a later split only after the first one's result was known");
    });

    private static void NoSecondSplitInFlight() => Clocked(() =>
    {
        var rig = World();
        ServerTrades();
        _onSplit = null;   // the server never answers
        Open(rig);
        Run(rig, 50);      // 5 s: the split's 10 s wait is not over
        Check.Eq(Splits.Count, 1, "one split in flight, no second one");
        Run(rig, 60);      // 11 s: the wait is over, the failure says what happened, one retry
        Check.True(FakeHost.Logs.Any(l => l.Contains("changed nothing (the server didn't split it; still x40)")), "the failure names the cause");
        Check.Eq(Splits.Count, 2, "a retry only after the first split's wait ended");
    });

    private static void CantBuySaysWhy() => Clocked(() =>
    {
        var rig = World(withNotes: false);
        Open(rig);
        Run(rig, 10);
        string want = "can't buy 36x Prismatic Taper (rule 'B: Prismatic Tapers'): 115 pyreals each, you have 0";
        Check.True(FakeHost.Logs.Any(l => l.Contains(want)), "the log says why");
        Check.True(FakeHost.Chat.Any(l => l.Contains(want)), "so does chat");
        Check.True(FakeHost.Logs.Any(l => l.Contains("AutoVendor finished: Town Vendor")), "the session end is in the log");
    });

    private static void LetterIsNoTradeNote() => Clocked(() =>
    {
        var rig = World(withNotes: false);
        AddStack(Letter, "Scarlet Red Letter", TypeWritable, 99001, 1, 5, 1);
        Open(rig);
        Run(rig, 10);
        Check.False(FakeHost.Logs.Any(l => l.Contains("Scarlet Red Letter") && l.Contains("needs Object Class data")),
                    "a writable is never a TradeNote: the Sell rule simply doesn't match it");
        Check.Eq(Sells.Count, 0, "and it isn't sold");
    });
}

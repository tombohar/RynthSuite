using System;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using RynthCore.Loot;
using RynthCore.Plugin.RynthAi;
using RynthCore.Plugin.RynthAi.ItemInfo;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.Plugin.Shared;
using RynthCore.PluginCore;
using RynthCore.PluginSdk;
using RynthCore.RynthAiHostTests.Fakes;
using RynthCore.RynthAiTests.Harness;

namespace RynthCore.RynthAiHostTests.Tests;

// Plugin-only fixes:
//  - a selection change sent duplicate identifies and health queries (the engine selects every
//    corpse it opens and every item it moves): creatures only, and no identify when cached;
//  - the fellowship name read scanned to a NUL without a bound.
// (The Shutdown exports' try/catch is an [UnmanagedCallersOnly] method: managed code cannot call
// it, so it has no test here.)
internal static class PluginFixTests
{
    private const uint Player = 0x50000F21;
    private const uint Wolf = 0x80400001, Dead = 0x80400002, Peas = 0x80400003, Wolf2 = 0x80400004;
    private const uint Ghost = 0x80400005, Ghost2 = 0x80400006, Ghost3 = 0x80400007, Ghost4 = 0x80400008, CorpseX = 0x80400009;
    private const BindingFlags Any = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static;

    public static void Register(Runner r)
    {
        r.Add("selection: selecting a creature whose appraisal is cached sends no identify", CachedCreatureNoId);
        r.Add("selection: selecting an uncached creature sends exactly one identify (and one health query)", UncachedCreatureOneId);
        r.Add("selection: selecting a corpse or an item sends no identify and no health query", CorpseAndItemNothing);
        r.Add("selection: the dashboard alone sends no health query", DashboardNoHealthQuery);
        r.Add("selection: a monster cached as Unknown (type not read yet) still gets its identify and health query", UnknownMonsterStillAsked);
        r.Add("selection: an Unknown that is typed as an item, or not attackable, gets nothing; a late creature flag does", UnknownNonCreatureNothing);
        r.Add("selection: no cache entry - a corpse is rejected by name, a creature by its flag", NullCacheFallback);
        r.Add("selection: creature classes are Monster, Player, Npc and CombatPet only", CreatureClasses);
        r.Add("fellowship name read: ReadableBytesFrom ends at the page whose neighbour is PAGE_NOACCESS", ReadableBytesAtGuardPage);
        r.Add("fellowship name read: it stops at 64 characters and at the end of the probed bytes", NameReadBounded);
        r.Add("fellowship table: an ACECustom 29-member fellowship is read, torn headers are not", FellowshipTableSizes);
    }

    // -- Selection ------------------------------------------------------------

    private sealed class World
    {
        public readonly RynthAiPlugin P = new();
        public readonly WorldObjectCache Cache;
        public readonly RynthCoreHost Host;
        public readonly LegacyDashboardRenderer Dash;

        public World()
        {
            FakeHost.Reset();
            FakeHost.WeaponCalls = true;   // RequestId + HasAppraisalData
            FakeHost.TargetCalls = true;   // QueryHealth + GetLastIdTime
            FakeHost.Names[Player] = "Reader";
            FakeHost.PlayerIdValue = Player;
            FakeHost.Positions[Player] = (0x1048002Au, 100f, 100f, 0f);
            FakeHost.PlayerPose = (0x1048002Au, 100f, 100f, 0f);

            Landscape(Wolf, "Grievver", 0x10);
            Landscape(Wolf2, "Shadow", 0x10);
            Landscape(Dead, "Corpse of Grievver", 0x10);   // a corpse keeps the creature flag
            FakeHost.Names[Peas] = "Pyreal Pea";
            FakeHost.Containers[Peas] = Player;
            FakeHost.ItemTypes[Peas] = 0x8;

            Host = FakeHost.Create(playerId: Player);
            typeof(RynthPluginBase).GetMethod("Attach", Any)!.Invoke(P, new object[] { FakeHost.LastApi });
            Dash = (LegacyDashboardRenderer)RuntimeHelpers.GetUninitializedObject(typeof(LegacyDashboardRenderer));
            typeof(LegacyDashboardRenderer).GetField("_settings", Any)!.SetValue(Dash, new LegacyUiSettings());
            typeof(LegacyDashboardRenderer).GetField("_host", Any)!.SetValue(Dash, Host);
            Cache = new WorldObjectCache(Host);
            Cache.SetPlayerId(Player);
            Set("_dashboard", Dash);
            Set("_objectCache", Cache);
            Set("_playerId", Player);
            Set("_initialized", true);
            Set("_loginComplete", true);
            Set("_lootInspectMode", false);   // not under test: keeps the loot-rule check out of the way
            foreach (uint id in new[] { Wolf, Wolf2, Dead, Peas }) Cache.OnCreateObject(id);
            Cache.Tick();
        }

        private static void Landscape(uint id, string name, uint type)
        {
            FakeHost.Names[id] = name;
            FakeHost.Positions[id] = (0x1048002Au, 105f, 100f, 0f);
            FakeHost.ItemTypes[id] = type;
        }

        public void Set(string f, object? v) => typeof(RynthAiPlugin).GetField(f, Any)!.SetValue(P, v);

        public void Select(uint id) => P.OnSelectedTargetChange(id, 0);
    }

    private static void CachedCreatureNoId()
    {
        var w = new World();
        FakeHost.Appraised.Add(Wolf);
        w.Select(Wolf);
        Check.Eq(FakeHost.IdRequests.Count, 0, "appraisal cached: no identify");
        Check.Eq(FakeHost.HealthQueries.Count, 1, "the creature's health is still asked once");

        FakeHost.IdTimes[Wolf2] = 1234;   // an engine that only reports GetLastIdTime
        w.Select(Wolf2);
        Check.Eq(FakeHost.IdRequests.Count, 0, "a reply on record (GetLastIdTime > 0) counts as cached");
    }

    private static void UncachedCreatureOneId()
    {
        var w = new World();
        w.Select(Wolf);
        Check.True(FakeHost.IdRequests.SequenceEqual(new[] { Wolf }), $"exactly one identify: [{string.Join(",", FakeHost.IdRequests.Select(i => i.ToString("X8")))}]");
        Check.True(FakeHost.HealthQueries.SequenceEqual(new[] { Wolf }), "and one health query");
    }

    private static void CorpseAndItemNothing()
    {
        var w = new World();
        SelfSelect.Mark(Peas);   // the engine's own pick (the looter moving an item)
        w.Select(Dead);
        w.Select(Peas);
        Check.Eq(FakeHost.IdRequests.Count, 0, "a corpse and an item: no identify");
        Check.Eq(FakeHost.HealthQueries.Count, 0, "and no health query");
    }

    private static void DashboardNoHealthQuery()
    {
        var w = new World();
        w.Dash.SetSelectedTarget(Wolf);
        w.Dash.SetSelectedTarget(Dead);
        w.Dash.SetSelectedTarget(Peas);
        Check.Eq(FakeHost.HealthQueries.Count, 0, "the dashboard's own selection handler sends no health query");
        Check.Eq(FakeHost.IdRequests.Count, 0, "nor an identify");
    }

    // -- Fellowship name read -------------------------------------------------

    private static void NameReadBounded()
    {
        const int Size = 256;
        IntPtr buf = Marshal.AllocHGlobal(Size);
        try
        {
            for (int i = 0; i < Size; i++) Marshal.WriteByte(buf, i, i < 200 ? (byte)'A' : (byte)0);   // 200 chars, then NULs
            string capped = FellowshipTracker.ReadBoundedAnsi(buf, FellowshipTracker.MaxNameChars, 200);
            Check.Eq(capped.Length, 64, "a long run is cut at 64 characters");

            string atPageEnd = FellowshipTracker.ReadBoundedAnsi(buf, FellowshipTracker.MaxNameChars, 10);
            Check.Eq(atPageEnd.Length, 10, "an unterminated run is cut where the probed bytes end");

            Marshal.WriteByte(buf, 5, 0);
            Check.Eq(FellowshipTracker.ReadBoundedAnsi(buf, FellowshipTracker.MaxNameChars, 200), "AAAAA", "a terminated name reads whole");
            Check.Eq(FellowshipTracker.ReadBoundedAnsi(buf, FellowshipTracker.MaxNameChars, 0), "", "nothing readable: empty");
            Check.Eq(FellowshipTracker.ReadBoundedAnsi(IntPtr.Zero, FellowshipTracker.MaxNameChars, 64), "", "null: empty");
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    private static void FellowshipTableSizes()
    {
        const int Buckets = 0x1000;   // any non-zero bucket array address
        Check.True(FellowshipTracker.IsPlausibleTable(Buckets, 16, 9), "a full retail fellowship (9)");
        Check.True(FellowshipTracker.IsPlausibleTable(Buckets, 64, 29), "an Infinite Leaftide fellowship (29, ACECustom MaxFellows)");
        Check.True(FellowshipTracker.IsPlausibleTable(Buckets, 1, 1), "one member, one bucket");
        Check.False(FellowshipTracker.IsPlausibleTable(0, 16, 3), "no bucket array");
        Check.False(FellowshipTracker.IsPlausibleTable(Buckets, 0, 3), "no buckets");
        Check.False(FellowshipTracker.IsPlausibleTable(Buckets, 16, 0), "no members");
        Check.False(FellowshipTracker.IsPlausibleTable(Buckets, FellowshipTracker.MaxBuckets + 1, 3), "a torn bucket count");
        Check.False(FellowshipTracker.IsPlausibleTable(Buckets, 16, FellowshipTracker.MaxMembers + 1), "a torn member count");
        Check.False(FellowshipTracker.IsPlausibleTable(Buckets, -5, 3), "a negative bucket count");
    }

    private static void Land(uint id, string name)
    {
        FakeHost.Names[id] = name;
        FakeHost.Positions[id] = (0x1048002Au, 106f, 100f, 0f);
    }

    private static void UnknownMonsterStillAsked()
    {
        var w = new World();
        Land(Ghost, "Drudge Skulker");          // no ItemTypes entry: the type is not readable yet
        Check.True(w.Cache[unchecked((int)Ghost)]?.ObjectClass == AcObjectClass.Unknown, "set-up: cached as Unknown");
        w.Select(Ghost);
        Check.True(FakeHost.IdRequests.SequenceEqual(new[] { Ghost }), "identify sent for the Unknown");
        Check.True(FakeHost.HealthQueries.SequenceEqual(new[] { Ghost }), "health asked for the Unknown");
    }

    private static void UnknownNonCreatureNothing()
    {
        var w = new World();
        Land(Ghost2, "Marble Statue");
        Land(Ghost3, "Mute Thing");
        Land(Ghost4, "Late Wolf");
        foreach (uint g in new[] { Ghost2, Ghost3, Ghost4 })
            Check.True(w.Cache[unchecked((int)g)]?.ObjectClass == AcObjectClass.Unknown, $"set-up: {g:X8} cached as Unknown");
        FakeHost.ItemTypes[Ghost2] = 0x8;       // the type arrives: an item
        FakeHost.NotAttackable.Add(Ghost2);
        FakeHost.NotAttackable.Add(Ghost3);     // never typed, but the engine says not attackable
        FakeHost.ItemTypes[Ghost4] = 0x10;      // the type arrives: a creature
        w.Select(Ghost2);
        w.Select(Ghost3);
        Check.Eq(FakeHost.IdRequests.Count, 0, "typed item / not attackable: no identify");
        Check.Eq(FakeHost.HealthQueries.Count, 0, "and no health query");
        w.Select(Ghost4);
        Check.True(FakeHost.IdRequests.SequenceEqual(new[] { Ghost4 }) && FakeHost.HealthQueries.SequenceEqual(new[] { Ghost4 }), "the late creature flag is honoured");
    }

    private static void NullCacheFallback()
    {
        var w = new World();
        w.Set("_objectCache", null);
        Land(CorpseX, "Corpse of Grievver");
        FakeHost.ItemTypes[CorpseX] = 0x10;     // a corpse keeps the creature flag
        w.Select(CorpseX);
        Check.Eq(FakeHost.IdRequests.Count + FakeHost.HealthQueries.Count, 0, "an uncached corpse is rejected by name");
        w.Select(Wolf);
        Check.True(FakeHost.IdRequests.SequenceEqual(new[] { Wolf }) && FakeHost.HealthQueries.SequenceEqual(new[] { Wolf }),
            "positive control: an uncached creature (flag 0x10) is asked");
    }

    private static void CreatureClasses()
    {
        var m = typeof(RynthAiPlugin).GetMethod("IsCreatureClass", Any)!;
        bool Is(AcObjectClass c) => (bool)m.Invoke(null, new object[] { c })!;
        foreach (var c in new[] { AcObjectClass.Monster, AcObjectClass.Player, AcObjectClass.Npc, AcObjectClass.CombatPet })
            Check.True(Is(c), $"{c} is a creature");
        foreach (var c in new[] { AcObjectClass.Corpse, AcObjectClass.Unknown, AcObjectClass.Vendor, AcObjectClass.Door, AcObjectClass.Portal })
            Check.False(Is(c), $"{c} is not");
    }

    // -- ReadableBytesFrom at a guard page ------------------------------------

    [DllImport("kernel32.dll")] private static extern IntPtr VirtualAlloc(IntPtr addr, UIntPtr size, uint type, uint protect);
    [DllImport("kernel32.dll")] private static extern bool VirtualProtect(IntPtr addr, UIntPtr size, uint protect, out uint old);
    [DllImport("kernel32.dll")] private static extern bool VirtualFree(IntPtr addr, UIntPtr size, uint type);

    private static void ReadableBytesAtGuardPage()
    {
        const uint CommitReserve = 0x3000, ReadWrite = 0x04, NoAccess = 0x01, Release = 0x8000;
        IntPtr p = VirtualAlloc(IntPtr.Zero, (UIntPtr)8192u, CommitReserve, ReadWrite);
        Check.True(p != IntPtr.Zero, "set-up: two pages allocated");
        if (p == IntPtr.Zero) return;
        try
        {
            Check.True(VirtualProtect(p + 4096, (UIntPtr)4096u, NoAccess, out _), "set-up: second page PAGE_NOACCESS");
            for (int i = 0; i < 4096; i++) Marshal.WriteByte(p, i, (byte)'A');   // no NUL before the guard page
            IntPtr s = p + 4090;
            Check.Eq(FellowshipTracker.ReadableBytesFrom(s), 6, "six readable bytes left before the guard page");
            Check.Eq(FellowshipTracker.ReadableBytesFrom(p + 4096), 0, "the guard page itself: none");
            Check.Eq(FellowshipTracker.ReadBoundedAnsi(s, FellowshipTracker.MaxNameChars, FellowshipTracker.ReadableBytesFrom(s)), "AAAAAA",
                "an unterminated name at the page end stops there, without touching the guard page");
        }
        finally { VirtualFree(p, UIntPtr.Zero, Release); }
    }}
